/// <reference path="create_account.html" />
(function() {
    'use strict';
    var controllerId = 'account';
    angular.module('avalanchain').controller(controllerId, ['common', 'dataservice', '$scope', '$filter', '$uibModal', '$rootScope', '$stateParams', '$interval', '$state' ,account]);

    function account(common, dataservice, $scope, $filter, $uibModal, $rootScope, $stateParams, $interval, $state) {
        var getLogFn = common.logger.getLogFn;
        var log = getLogFn(controllerId);
        var vm = this;

        vm.accounts= [];
        var accountId = $stateParams.accountId;
        vm.accountid = accountId;
        dataservice.getAccs().then(function(data) {
            vm.users = data.data;
            for (var pr in data.data) {
                var acc = data.data[pr];
                var property = '';
                for (var prop in acc.status) {
                    if (acc.status.hasOwnProperty(prop)){
                        property =  prop;
                        break;
                    }

                }
                vm.accounts.push({
                    name: acc.account.accountId.replace(/-/gi, ''),
                    publicKey: acc.account.accountId.replace(/-/gi, ''),
                    balance: acc.balance,
                    status: property,
                    signed: true,
                    expired: acc.account.expire,
                    ref: {
                        address: acc.account.accountId.replace(/-/gi, '')
                    }
                });
            }
          $scope.accounts = vm.accounts;
            $scope.current = vm.accounts.filter(function(acc) {
                return acc.ref.address === accountId;
            })[0];
            if(!$scope.current){
                $state.go('index.accounts');
            }
            $scope.getTransactions();

        });

        vm.transactions = [];

        $scope.payment = {
            fromAcc: {},
            toAcc: {}
        };
        //TODO: pagination add to service
        $scope.maxSize = 5;
        $scope.totalItems = [];
        $scope.currentPage = 1;
        $scope.transactionPage = 1;

        // $scope.Timer = setInterval(function updateRandom() {
        //     $scope.getTransactions();
        // }, 3000);


        $scope.getTransactions = function() {
            dataservice.getData().then(function(data) {
                vm.transactions = data.transactions.filter(function(transaction) {
                    return transaction.from === accountId || transaction.to === accountId;
                });

                var currentTransactionPage = $scope.transactionPage;
                $scope.payment.fromAcc = $scope.current.ref;
            });
        }

        $scope.sendPayment = function() {
            dataservice.sendPayment($scope.payment).then(function(data) {
                $scope.getTransactions($scope.current.ref.address);
                getAccounts();
            });
        }

        // function addStatus(data) {
        //     if (data) {
        //         for (var i = 0; i < data.length; i++) {
        //             if (data[i].status === 1) {
        //                 data[i]["navigation"] = 'label-primary';
        //             } else {
        //                 data[i]["navigation"] = 'label-deafault';
        //             }
        //         }
        //     }
        //     return data;
        // }

        $scope.startTimer = function() {
            $scope.Timer = $interval($scope.getTransactions, 3000);
        };

        //TODO: add to service
        $scope.$on("$destroy", function() {
            if (angular.isDefined($scope.Timer)) {
                $interval.cancel($scope.Timer);
            }
        });
        $scope.startTimer();

        activate();

        function activate() {
            common.activateController([], controllerId)
                .then(function() {}); //log('Activated Admin View');
        }

    };


})();
