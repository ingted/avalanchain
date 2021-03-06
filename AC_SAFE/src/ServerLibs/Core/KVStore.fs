namespace Avalanchain.Core


module KVStore =
    open System
    open System.Collections.Generic
    open System.Data.Common
    open System.Threading.Tasks
    open FSharp.Control.Tasks
    open FSharp.Control.Tasks.ContextInsensitive
    open FSharp.Control.Reactive
    open FSharpx.Result
    open LightningDB
    open EtcdGrpcClient
    // open Akavache
    open System.Reactive.Concurrency
    open System.Reactive.Linq
    open System.Reactive.Threading.Tasks
    open Crypto
    open ChainDefs
    open Database

    type ValueIssue = 
        | ValueSetIssue of ValueSetIssue
        | ValueAccessIssue of ValueAccessIssue
    and ValueSetIssue =
        | ValueAlreadySet
        | WriteAccessDenied
        | WriteTechIssue of string
    and ValueAccessIssue = 
        | NoDataExists 
        | ReadAccessDenied
        | DataIntegrityFailure of string
        | ReadTechIssue of string

    type ValueSlot = ValueSlot of string
        with member __.Value = match __ with ValueSlot s -> s
          
    type KeySerializer<'TK> = {
        Serializer: 'TK -> string
        Deserializer: string -> 'TK 
    }

    [<Interface>]
    type IKVStore<'TK> = 
        abstract member Get : key: 'TK -> Task<Result<ValueSlot, ValueIssue>>
        abstract member GetRange : keyPrefix: 'TK * pageSize: PageSize -> Task<IDictionary<'TK, Result<ValueSlot, ValueIssue>>>
        abstract member GetRange : 'TK * 'TK * pageSize: PageSize -> Task<IDictionary<'TK, Result<ValueSlot, ValueIssue>>>
        abstract member Put : key: 'TK * value: string -> Task<Result<unit, ValueIssue>>
        abstract member Put : keyValues: ('TK * string)[] -> Task<Result<unit, ValueIssue>>

    module PagedLog =
        open System.Reactive.Subjects

        type LogView = {
            GetCount:    unit ->            Task<Result<Pos, ValueIssue>>
            GetPage:     Pos -> PageSize -> Task<IDictionary<Pos, Result<ValueSlot, ValueIssue>>>
            GetLastPage: PageSize ->        Task<IDictionary<Pos, Result<ValueSlot, ValueIssue>>>
        }
        
        type Log = {
            OfferAsync:  string -> Task<Result<unit, ValueIssue>>  
            View:        LogView
        }
        
        type LogKey = 
            | Length of LogId: string
            | Item of LogId: string * Pos: Pos
            
        let logKeySerializer prefix = { Serializer = function   | Length logId      -> sprintf "%s%s.--length--" prefix logId
                                                                | Item (logId, pos) -> sprintf "%s%s.%010i" prefix logId pos
                                        Deserializer = fun s -> 
                                                            let pr = s.Substring(0, prefix.Length)
                                                            let logId = s.Substring(prefix.Length, s.Length - 11)
                                                            let last = s.Substring (s.Length - 10, 10) 
                                                            match last with 
                                                            | "--length--" -> Length(logId)
                                                            | l -> match UInt64.TryParse l with 
                                                                    | (true, pos) -> Item(logId, pos) 
                                                                    | _ -> failwithf "Incorrect Key %s" s }
            
        type TaskBuilder = 
            member __.Bind(m: Task<Result<'a, 'e>>, f: 'a -> Result<Task<'b>, 'e>) = task { let! mr = m
                                                                                            let res = result {
                                                                                                let! inner = mr
                                                                                                return! f inner  
                                                                                            }
                                                                                            return! match res with 
                                                                                                    | Ok t -> task {    let! ret = t 
                                                                                                                        return Ok ret }
                                                                                                    | Error e -> e |> Error |> Task.FromResult }
            member __.Bind(m: Result<'a, 'e>, f: 'a -> Result<'b, 'e>) = Result.bind f m |> Task.FromResult
                                                                                            
        
        let createKVBasedLogView (store: IKVStore<LogKey>) logId = 
            let setLength (length: Pos) = task {
                let! putResult = store.Put(Length logId, length.ToString())
                return match putResult with | Error (ValueSetIssue(ValueAlreadySet)) -> Ok ()
                                            | res -> res  
            }
            let getLength() = task {
                let! lengthResult = store.Get(Length logId)  
                return! match lengthResult with 
                        | Error (ValueAccessIssue(NoDataExists)) -> task {
                                let! setLengthResult = setLength 0UL
                                return setLengthResult |> Result.map (fun _ -> 0UL)  
                            } 
                        | res -> result {   let! v = res 
                                            return! match UInt64.TryParse v.Value with 
                                                    | (true, length) -> Ok length 
                                                    | _ -> sprintf "Incorrect Length field format '%s'" v.Value 
                                                            |> DataIntegrityFailure
                                                            |> ValueAccessIssue 
                                                            |> Error } 
                                    |> Task.FromResult
            }
            
            let extractPos = function   | Item (_, pos) -> pos
                                        | Length _ -> failwith "Inconsistent Data"
                                                    
            let getPage from pageSize = task { 
                let! page = store.GetRange (Item (logId, from), pageSize) 
                let page = page |> Seq.toArray
                return page 
                        |> Seq.map (fun kv -> extractPos kv.Key, kv.Value) 
                        |> Map.ofSeq
                        :> IDictionary<Pos, Result<ValueSlot, ValueIssue>>
            }
            let getLastPage pageSize = task {
                let! length = getLength()
                return! match length with 
                        | Ok len -> if len <= uint64(pageSize) then getPage 0UL (uint32 len)
                                    else getPage (len - uint64(pageSize) - 1UL) pageSize
                        | Error e -> failwith (e.ToString()) // e |> Error |> Task.FromResult   // TODO: Investigate other error reporting options 
            }

            let view = {   LogView.GetCount =   getLength  
                           GetPage =            getPage
                           GetLastPage =        getLastPage }
                           
            let offer str = task {
                let! lastPos = getLength()
                return! match lastPos with 
                            | Ok lastPos -> 
                                let newPos = lastPos + 1UL
                                store.Put([|    Item (logId, lastPos), str 
                                                (Length logId), newPos.ToString() |]) // TODO: Handle error
                            | Error e -> e.ToString() |> WriteTechIssue |> ValueSetIssue |> Error |> Task.FromResult
            }

            {   OfferAsync = offer
                View       = view }
            
/// Sqlite
        let createLogView (connectionReadOnly: #DbConnection) logId = task {
            let tableName = tableName logId
           
            let getLength() = task {
                let! lengthResult = getMaxId connectionReadOnly tableName
                return match lengthResult with 
                        | Error exn -> exn.Message |> ReadTechIssue |> ValueAccessIssue |> Error
                        | Ok None -> Ok 0UL
                        | Ok (Some i) -> Ok i
            }
            
            let getPage from pageSize = task { 
                let! page = Database.getPage connectionReadOnly tableName from pageSize
                return match page with
                        | Ok items -> items
                                        |> Seq.map (fun ci -> ci.Id, (ci.Data |> ValueSlot |> Ok))
                                        |> Map.ofSeq
                                        :> IDictionary<Pos, Result<ValueSlot, ValueIssue>>
                        | Error exn -> failwith (exn.ToString()) // e |> Error |> Task.FromResult   // TODO: Investigate other error reporting options                  
            }
            let getLastPage pageSize = task {
                let! length = getLength()
                return! match length with 
                        | Ok len -> if len <= uint64(pageSize) then getPage 0UL (uint32 len)
                                    else getPage (len - uint64(pageSize) - 1UL) pageSize
                        | Error e -> failwith (e.ToString()) // e |> Error |> Task.FromResult   // TODO: Investigate other error reporting options 
            }

            let view = {   LogView.GetCount =   getLength  
                           GetPage =            getPage
                           GetLastPage =        getLastPage }
            
            return view               
        }

        // let sink = new Subject<ChainItem>()
        
        // let flow = 
        //     sink
        //     |> Observable.iter (printfn "%A")
        //     |> Observable.bufferSpanCount (TimeSpan.FromMilliseconds(100.)) 10000
        //     //|> Observable.bind(fun o -> Observable.FromAsync(fun () -> insertBatch connection tableName o))
        //     |> Observable.subscribe (fun _ -> 
        //                                         printfn "Commited batch for logId: '%s'" "logId"
        //                                         ()    )

        let sink = new Subject<ChainItem>()
        

        // let connectionString = """DataSource=:memory:; Cache = Shared"""
        let connectionString = """DataSource=./database.sqlite; Cache = Shared"""

        let flow = 
            sink
            |> Observable.bufferSpanCount (TimeSpan.FromMilliseconds(1000.)) 10000
            |> Observable.subscribe (fun o -> 
                                            use connection = connect connectionString
                                            insertBatch connection o
                                            printfn "Commited batch for '%d' items" o.Count
                                            ()    )

        let createLog (connection: #DbConnection) (connectionReadOnly: #DbConnection) logId = task {
            let tableName = tableName logId
            let! createTableResult = createTable connection tableName
            match createTableResult with
            | Error e -> failwithf "Table creation Exception: '%A'" e
            | Ok _ -> () 
            
            // let sink = new Subject<ChainItem>()
            
            // let flow = 
            //     sink
            //     // |> Observable.iter (printfn "%A")
            //     |> Observable.bufferSpanCount (TimeSpan.FromMilliseconds(100.)) 10000
            //     // |> Observable.bind(fun o -> Observable.FromAsync(fun () -> insertBatch connection tableName o))
            //     // |> Observable.map(fun o -> insertBatch connection tableName o)
            //     // |> Observable.subscribeOn(Scheduler.CurrentThread)
            //     |> Observable.subscribe (fun o -> 
            //                                     insertBatch connection o
            //                                     printfn "Commited batch for logId: '%s'" logId
            //                                     ()    )

            let offer str = task {
                //let! res = insert connection tableName { ChainItem.Id = 0UL; Data = str; Hash = str.Substring 10 }
//                return match res with 
//                        | Ok _ -> Ok ()
//                        | Error e -> e.ToString() |> WriteTechIssue |> ValueSetIssue |> Error
                sink.OnNext { ChainItem.Id = 0UL; Data = str; Hash = str.Substring 10; TableName = tableName }
                return Ok () // TODO: Add error handling 
            }
            
            let! view = createLogView connectionReadOnly logId

            return {OfferAsync = offer
                    View       = view }            
        }
        

    type IHashStore = IKVStore<Hash>

    let newLightningEnvironment envName =
        let env = new LightningEnvironment(envName)
        env.MaxDatabases <- 10
        env.MapSize <- 1073741824L
        env.MaxReaders <- 1000
        env.Open(EnvironmentOpenFlags.NoThreadLocalStorage ||| EnvironmentOpenFlags.NoSync)
        env


    type LmdbKVStore<'TK when 'TK: comparison>(keySerializer: KeySerializer<'TK>, env: LightningEnvironment, dbName, putOptions: PutOptions) =
        let keyBytes = keySerializer.Serializer >> getBytes
        let valueBytes = ValueSlot >> toJson >> getBytes
        let toKey = getString >> keySerializer.Deserializer
        let writeTimeStamp() =
            use tx = env.BeginTransaction()
            use db = tx.OpenDatabase(dbName, new DatabaseConfiguration (Flags = DatabaseOpenFlags.Create ))
            tx.Put(db, "LastAccessed" |> getBytes |> ArraySegment, DateTimeOffset.Now.ToString() |> getBytes |> ArraySegment, putOptions)
            tx.Commit()
        do writeTimeStamp()
        interface IKVStore<'TK> with
            member __.Get key =
                use tx = env.BeginTransaction(TransactionBeginFlags.ReadOnly)
                use db = tx.OpenDatabase(dbName)
                //let found, result = tx.TryGet(db, keyBytes key) // TODO: Add error handling
                use cursor = tx.CreateCursor(db)
                let found = cursor.MoveTo(keyBytes key)
                if found then
                    let result = cursor.GetCurrent().Value
                    result |> getString |> fromJson<ValueSlot> |> Ok
                else NoDataExists |> ValueAccessIssue |> Error
                |> Task.FromResult
            member __.GetRange (keyPrefix, pageSize) = 
                use tx = env.BeginTransaction(TransactionBeginFlags.ReadOnly)
                use db = tx.OpenDatabase(dbName)
                use cursor = tx.CreateCursor(db)
                let mutable i = pageSize
                let mutable hasCurrent = keyPrefix |> keyBytes |> cursor.MoveToFirstAfter
                let ar = ResizeArray<'TK * Result<ValueSlot, ValueIssue>>()

                while i > 0u && hasCurrent do
                    let key = cursor.Current.Key |> toKey
                    let value = cursor.Current.Value |> getString |> fromJson<ValueSlot> |> Ok 
                    ar.Add(key, value)
                    hasCurrent <- cursor.MoveNext()
                    i <- i - 1u
                   
                ar  |> Map.ofSeq 
                    :> IDictionary<'TK, Result<ValueSlot, ValueIssue>> 
                    |> Task.FromResult
           
            member __.GetRange (keyFrom, keyTo, pageSize) = 
                use tx = env.BeginTransaction(TransactionBeginFlags.ReadOnly)
                use db = tx.OpenDatabase(dbName)
                use cursor = tx.CreateCursor(db)
                let mutable i = pageSize
                let mutable hasCurrent = keyFrom |> keyBytes |> cursor.MoveToFirstAfter
                let ar = ResizeArray<'TK * Result<ValueSlot, ValueIssue>>()

                while i > 0u && hasCurrent do
                    let key = cursor.Current.Key |> toKey
                    hasCurrent <- 
                        if (keyTo = key) then false
                        else 
                            let value = cursor.Current.Value |> getString |> fromJson<ValueSlot> |> Ok
                            ar.Add(key, value)
                            cursor.MoveNext()
                    i <- i - 1u
                   
                ar  |> Map.ofSeq 
                    :> IDictionary<'TK, Result<ValueSlot, ValueIssue>> 
                    |> Task.FromResult
            member __.Put (key, value) = 
                use tx = env.BeginTransaction()
                use db = tx.OpenDatabase(dbName, new DatabaseConfiguration (Flags = DatabaseOpenFlags.Create ))
                let mutable keyB = keyBytes key |> ArraySegment
                let mutable valB = valueBytes value |> ArraySegment
                tx.Put(db, keyB, valB, putOptions)
                tx.Commit()
                () |> Ok |> Task.FromResult
            member __.Put keyValues = 
                use tx = env.BeginTransaction()
                use db = tx.OpenDatabase(dbName, new DatabaseConfiguration (Flags = DatabaseOpenFlags.Create ))
                for (key, value) in keyValues do
                    tx.Put(db, keyBytes key |> ArraySegment, valueBytes value |> ArraySegment, putOptions)
                tx.Commit()
                () |> Ok |> Task.FromResult

   
    type EtcdKVStore<'TK when 'TK: comparison>(keySerializer: KeySerializer<'TK>, etcdClient: EtcdClient) =
        let keyBytes = keySerializer.Serializer 
        let valueBytes = ValueSlot >> toJson
        interface IKVStore<'TK> with
            member __.Get key = task {
                let! result = etcdClient.Get(keyBytes key)
                return  if isNull result then NoDataExists |> ValueAccessIssue |> Error
                        else result |> fromJson<ValueSlot> |> Ok
            }
            member __.GetRange (keyPrefix, pageSize) = task {
                let! result = etcdClient.GetRange(keyBytes keyPrefix)
                return  if isNull result then Map.empty
                        else seq { for kv in result |> Seq.truncate (int pageSize) -> 
                                    kv.Key |> keySerializer.Deserializer, kv.Value |> fromJson<ValueSlot> |> Ok } 
                                |> Map.ofSeq
                        :> IDictionary<'TK, Result<ValueSlot, ValueIssue>> 
            }
            member __.GetRange (keyFrom, keyTo, pageSize) = task {
                let! result = etcdClient.GetRange(keyBytes keyFrom, keyBytes keyTo)
                return  if isNull result then Map.empty
                        else seq { for kv in result |> Seq.truncate (int pageSize) -> 
                                    kv.Key |> keySerializer.Deserializer, kv.Value |> fromJson<ValueSlot> |> Ok } 
                                |> Map.ofSeq
                        :> IDictionary<'TK, Result<ValueSlot, ValueIssue>> 
            }
            member __.Put (key, value) = task {
                let! result = etcdClient.Put(keyBytes key, value)
                return  if result |> isNull |> not then () |> Ok
                        else WriteTechIssue "No response" |> ValueSetIssue |> Error 
            }
            member __.Put keyValues = task {
                for (key, value) in keyValues do
                    let! result = etcdClient.Put(keyBytes key, value) // TODO: Add per-key error reporting
                    ()
                return  () |> Ok
            }

    // type AkavacheKVStore<'TK when 'TK: comparison>(keySerializer: KeySerializer<'TK>, blobPath: string option) =
    //     let keyBytes = keySerializer.Serializer 
    //     let valueBytes = ValueSlot >> toJson
    //     do BlobCache.ApplicationName <- "Avalanchain"
    //     let blobPath = defaultArg blobPath "./AC.akv"
    //     let blob = new Akavache.Sqlite3.SQLitePersistentBlobCache(blobPath)
    //     interface IKVStore<'TK> with
    //         member __.Get key = task {
    //             let! result = blob.Get([keyBytes key]).FirstAsync().ToTask()
    //             return  if result.Count = 0 then NoDataExists |> ValueAccessIssue |> Error
    //                     else result |> Seq.head |> (fun kv -> kv.Value |> getString |> fromJson<ValueSlot>) |> Ok
    //         }
    //         member __.GetRange (keyPrefix, pageSize) = task {
    //             let! result = blob.Get([keyBytes key]).FirstAsync().ToTask()
    //             return  if isNull result then Map.empty
    //                     else seq { for kv in result |> Seq.truncate (int pageSize) -> 
    //                                 kv.Key |> keySerializer.Deserializer, kv.Value |> fromJson<ValueSlot> |> Ok } 
    //                             |> Map.ofSeq
    //                     :> IDictionary<'TK, Result<ValueSlot, ValueIssue>> 
    //         }
    //         member __.GetRange (keyFrom, keyTo, pageSize) = task {
    //             let! result = etcdClient.GetRange(keyBytes keyFrom, keyBytes keyTo)
    //             return  if isNull result then Map.empty
    //                     else seq { for kv in result |> Seq.truncate (int pageSize) -> 
    //                                 kv.Key |> keySerializer.Deserializer, kv.Value |> fromJson<ValueSlot> |> Ok } 
    //                             |> Map.ofSeq
    //                     :> IDictionary<'TK, Result<ValueSlot, ValueIssue>> 
    //         }
    //         member __.Put (key, value) = task {
    //             let! result = etcdClient.Put(keyBytes key, value)
    //             return  if result |> isNull |> not then () |> Ok
    //                     else WriteTechIssue "No response" |> ValueSetIssue |> Error 
    //         }
    //         member __.Put keyValues = task {
    //             for (key, value) in keyValues do
    //                 let! result = etcdClient.Put(keyBytes key, value) // TODO: Add per-key error reporting
    //                 ()
    //             return  () |> Ok
    //         }            