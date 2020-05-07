using DbCommon;
using IBApi;
using SqCommon;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Utils = SqCommon.Utils;

namespace VirtualBroker
{
    public class BrokerTaskTradeStrategy : BrokerTask
    {
        public IBrokerStrategy Strategy { get; set; }

        public static BrokerTask BrokerTaskFactoryCreate()
        {
            return new BrokerTaskTradeStrategy();
        }

        internal override void Run()
        {
            Console.WriteLine();
            Utils.Logger.Info($"****  StrategyBrokerTask.Run() starts. BrokerTask Name: {BrokerTaskSchema.Name}");
            Utils.ConsoleWriteLine(ConsoleColor.Cyan, true, $"****  BrokerTask starts: '{BrokerTaskSchema.Name}'");
            StringBuilder detailedReportSb = new StringBuilder();
            detailedReportSb.AppendLine($"<font color=\"#000080\"><strong>{DateTime.UtcNow.ToString("MM-dd'T'HH':'mm':'ss': '")}'{BrokerTaskSchema.Name}'</strong></font>");

            //************************* 0. Initializations
            var strategyFactoryCreate = (Func<IBrokerStrategy>)BrokerTaskSchema.Settings[BrokerTaskSetting.StrategyFactory];
            Strategy = strategyFactoryCreate();


            //************************* 1. Get currentPortfolios for all users from sql DB
            List<BrokerTaskPortfolio> portfolios = (List<BrokerTaskPortfolio>)BrokerTaskSchema.Settings[BrokerTaskSetting.Portfolios];
            // http://stackoverflow.com/questions/1817300/convert-listderivedclass-to-listbaseclass
            if (!SqlTools.LoadHistoricalPipsFromDbAndCalculateTodayPips(portfolios.Cast<DbPortfolio>().ToList()).Result) // shallow Copy the list with Pointers. Fine. It has only 1 or 2 portfolios
                return;
            foreach (var portfolio in portfolios)
            {
                //Console.WriteLine($"Prtf:'{portfolio.Name}':");    // there are long portfolio names. Console messages should be shorter
                Utils.Logger.Info($"{portfolio.Name}: nTodayPips: {portfolio.TodayPositions.Count}.");
                portfolio.TodayPositions.ForEach(pip => Utils.Logger.Info($"{pip.DebugString()}: Volume: {pip.Volume:F2}."));

                if (!Controller.g_gatewaysWatcher.IsGatewayConnected(portfolio.IbGatewayUserToTrade))
                {
                    portfolio.IsErrorOccured = true;
                    if (!Controller.IsRunningAsLocalDevelopment())
                    {
                        // this will send Error message to HealthMonitor
                        StrongAssert.Fail(Severity.NoException, $"ERROR! Gateway for user {portfolio.IbGatewayUserToTrade} is not connected. Other portfolios may be fine. We continue.");
                    } else
                    {
                        Utils.Logger.Warn($"Warning, but Not Error in development! Gateway for user {portfolio.IbGatewayUserToTrade} is not connected. Other portfolios may be fine. We continue.");
                    }
                }
            }

            //************************* 2. Generate futurePortfolios with weights
            // most of the time, it is enough to run the strategy to calculate weights once, and play it for many userPortfolios with the same weights. However, sometimes maybe Strategy has to be run for each user separately
            Strategy.Init(detailedReportSb);
            List<PortfolioPositionSpec> proposedPositionSpecs = null;
            // try to make IsSameStrategyForAllUsers true, because then it is enough to run the complex Strategy calculation only once, 
            // Strategy gives only a guideline for the BrokerTask, who customize it to portfolios. Modify Leverage (1x, 2x), or change trading instrument (long XIV, instead of short VXX)
            if (Strategy.IsSameStrategyForAllUsers)
            {
                proposedPositionSpecs = Strategy.GeneratePositionSpecs(null); // Strategy calculates suggested positions only once (it may be a long calculation, like Neural Network), and use it for all portfolios
                StrongAssert.True(proposedPositionSpecs != null, Severity.ThrowException, "Error. Strategy.GeneratePositionSpecs() returned null. There is no point to continue. Crash this thread here.");
                proposedPositionSpecs.ForEach(suggestedItem =>
                {
                    string logMsg = $"Strategy suggestion: {suggestedItem.Ticker}: {suggestedItem.PositionType}-{suggestedItem.Size}";
                    Utils.ConsoleWriteLine(null, false, logMsg);
                    Utils.Logger.Info(logMsg);
                });
                double proposedTotalLeverage = proposedPositionSpecs.Sum(r => (r.Size is WeightedSize) ? Math.Abs(((WeightedSize)r.Size).Weight) : throw new NotImplementedException()  );
                StrongAssert.True(proposedTotalLeverage <= 3.0, Severity.ThrowException, $"Error. Proposed TotalLeverage ({proposedTotalLeverage}) is unexpectedly high. It is safer to Crash this thread here.");

                for (int i = 0; i < portfolios.Count; i++)
                {
                    portfolios[i].ProposedPositionSpecs = proposedPositionSpecs;
                }
            }
            else
            {
                for (int i = 0; i < portfolios.Count; i++)
                {
                    string logMsg = $"Portfolio of user {portfolios[i].IbGatewayUserToTrade.ToShortFriendlyString()}";
                    Utils.ConsoleWriteLine(null, false, logMsg);
                    Utils.Logger.Info(logMsg);
                    proposedPositionSpecs = Strategy.GeneratePositionSpecs(portfolios[i].Param); // Strategy calculates suggested positions only once (it may be a long calculation, like Neural Network), and use it for all portfolios
                    StrongAssert.True(proposedPositionSpecs != null, Severity.ThrowException, "Error. Strategy.GeneratePositionSpecs() returned null. There is no point to continue. Crash this thread here.");
                    proposedPositionSpecs.ForEach(suggestedItem =>
                    {
                        logMsg = $"Strategy suggestion: {suggestedItem.Ticker}: {suggestedItem.PositionType}-{suggestedItem.Size}";
                        Utils.ConsoleWriteLine(null, false, logMsg);
                        Utils.Logger.Info(logMsg);
                    });
                    double proposedTotalLeverage = proposedPositionSpecs.Sum(r => (r.Size is WeightedSize) ? Math.Abs(((WeightedSize)r.Size).Weight) : throw new NotImplementedException());
                    StrongAssert.True(proposedTotalLeverage <= 3.0, Severity.ThrowException, $"Error. Proposed TotalLeverage ({proposedTotalLeverage}) is unexpectedly high. It is safer to Crash this thread here.");

                    portfolios[i].ProposedPositionSpecs = proposedPositionSpecs;
                }
            }



            //*************************  3. Get realtime prices and Generate futurePortfolios with volumes and send transactions
            for (int i = 0; i < portfolios.Count; i++)
            {
                if (portfolios[i].IsErrorOccured)
                    continue;
                //************************* 3.1  Generate futurePortfolios with volumes, number of proposed shares
                CalculatePortfolioUsdSizeFromRealTime(portfolios[i], detailedReportSb);
                DetermineProposedPositions(portfolios[i]);

                //************************* 3.2  Generate Transactions from the difference of current vs. target
                DetermineProposedTransactions(portfolios[i]);
                
                //************************* 3.3 Play transactions, don't play too small transactions, however only VbGateway can decide what is small transaction, not VBroker
                // For example, selling 2 VXX is small in itself, but if VbGateway has another big VXX (MOC) transaction at the same time from another VBroker, than VbGateway can decide to play that 2 VXX too.
                PlaceTransactionsViaBroker(portfolios[i], detailedReportSb);
            }

            //*************************  4. Wait until GatewaysManager tells that orders are ready or until timout from expected time
            bool wasAllOrdersOk = true;
            StringBuilder errorStr = new StringBuilder();
            for (int i = 0; i < portfolios.Count; i++)
            {
                if (portfolios[i].IsErrorOccured)
                    continue;
                if (!WaitTransactionsViaBrokerAndCollectExecutionInfo(portfolios[i], errorStr))
                    wasAllOrdersOk = false;
            }
            if (!wasAllOrdersOk)
            {
                string errStr = "Not all trading orders were OK. " + errorStr.ToString();
                detailedReportSb.AppendLine($"<font color=\"#ff1010\">***ERROR. {errStr}</font>");
                Utils.Logger.Error(errStr);
            }

            // ********************* 5. Write All Transactions into DB in one go, not one by one
            WriteAllExecutedTransactionsToDB(portfolios);

            // ******************* 6. send OK or Error reports to HealthMonitor
            if (!Controller.IsRunningAsLocalDevelopment())      // When VBroker.exe is developed, we don't want to run HealthMonitor.exe every time on localhost
            {
                string briefReport = (wasAllOrdersOk) ? $"BrokerTask {BrokerTaskSchema.Name} was OK." : $"BrokerTask {BrokerTaskSchema.Name} had ERROR. {errorStr.ToString()} Inform supervisors to investigate log files for more detail. ";
                string healthMonitorMsg = $"<BriefReport>{briefReport}</BriefReport><DetailedReport>{detailedReportSb.ToString().Replace(Environment.NewLine, "<br>")}</DetailedReport>";

                if (!new HealthMonitorMessage()
                {
                    ID = (wasAllOrdersOk) ? HealthMonitorMessageID.ReportOkFromVirtualBroker : HealthMonitorMessageID.ReportErrorFromVirtualBroker,
                    ParamStr = healthMonitorMsg,
                    ResponseFormat = HealthMonitorMessageResponseFormat.None
                }.SendMessage().Result) // Task.Result will block this calling thread as Wait(), and runs the worker task in another ThreadPool thread. Checked.
                    Utils.Logger.Error("Error in sending HealthMonitorMessage to Server.");
            }


            Utils.Logger.Info($"StrategyBrokerTask.Run() ends.");
            Utils.ConsoleWriteLine(ConsoleColor.Yellow, true, $"****  BrokerTask ends: '{BrokerTaskSchema.Name}'");
        }



        public void CalculatePortfolioUsdSizeFromRealTime(BrokerTaskPortfolio p_portfolio, StringBuilder p_detailedReportSb)
        {
            var rtPrices = new Dictionary<int, PriceAndTime>() { { TickType.MID, new PriceAndTime() } };    // MID is the most honest price. LAST may happened 1 hours ago

            StringBuilder sbCalcPfSize = new StringBuilder("CalcPfSizeRT(): ");
            double portfolioUsdSize = 0;
            foreach (PortfolioPosition pip in p_portfolio.TodayPositions)
            {
                if (pip.AssetTypeID == AssetType.HardCash)
                {
                    if ((CurrencyID)pip.SubTableID == CurrencyID.USD)
                        portfolioUsdSize += pip.Volume; // for Cash, the Volume = 1 in the SQL DB, but we store the Value in the Volume, not in the Price, because of possible future other currencies 
                    else
                        throw new Exception("Only USD is implemented"); // in the future may use fixed or estimated or real time Forex prices
                }
                else
                {
                    int stockID = pip.SubTableID;
                    // TODO: we need a tickerProvider here
                    Contract contract = new Contract() { Symbol = Strategy.StockIdToTicker(stockID), SecType = "STK", Currency = "USD", Exchange = "SMART" };
                    double rtPrice = 0.0;
                    StrongAssert.True(Controller.g_gatewaysWatcher.GetAlreadyStreamedPrice(contract, ref rtPrices), Severity.ThrowException, $"GetAlreadyStreamedPrice({contract.Symbol}) failed. There is no point continuing if portfolioUSdSize cannot be calculated. After that we cannot calculate new stock Volumes from weights.");
                    rtPrice = rtPrices[TickType.MID].Price;

                    //double rtPrice = GetAssetIDRealTimePrice(BrokerTask.TaskLogFile, p_brokerAPI, pip.AssetID);
                    double positionUsdSize = pip.Volume * rtPrice;   // pip.Volume is signed. For shorts, it is negative, but that is OK.
                    string pfSizeStr = $"{contract.Symbol}: {pip.Volume}*{rtPrice:F2}={positionUsdSize:n0};";
                    sbCalcPfSize.Append(pfSizeStr);
                    Utils.Logger.Debug($"CalcPfSize():{pfSizeStr}");
                    portfolioUsdSize += positionUsdSize;
                }
            }
            p_portfolio.PortfolioUsdSize = portfolioUsdSize;
            // 2019-12-23, UGAZ had a 10 to 1 split, which was not in the DB. Short UGAZ was shown instead of profit, a huge loss. And negative PV. 
            // That can cause later silly trades. We should StrongAssert it, and we might crash the application. This is an error, and the error can be fatal later.
            // we should use user specific maximum bounds here
            StrongAssert.True((portfolioUsdSize > 0) && (portfolioUsdSize < 3* p_portfolio.MaxTradeValueInCurrency), Severity.ThrowException,
                $"PortfolioID '{p_portfolio.PortfolioID}', realtime PV: ${portfolioUsdSize:F0}  should be positive and less than a sensible value. Possible reason: missing split in DB. We terminate strategy execution to avoid 'silly' trades. {sbCalcPfSize.ToString()}");
            
            string logMsg = $"{p_portfolio.IbGatewayUserToTrade.ToShortFriendlyString()}: PortfolioID '{p_portfolio.PortfolioID}': realtime PV: ${p_portfolio.PortfolioUsdSize:F0}";
            Utils.ConsoleWriteLine(null, false, logMsg);
            Utils.Logger.Info(logMsg);
            p_detailedReportSb.AppendLine($"{p_portfolio.IbGatewayUserToTrade.ToShortFriendlyString()}: PV: ${p_portfolio.PortfolioUsdSize/1000.0:F2}K");
        }

        private void DetermineProposedPositions(BrokerTaskPortfolio p_portfolio)
        {
            double leverage = Strategy.GetPortfolioLeverage(p_portfolio.ProposedPositionSpecs, p_portfolio.Param);
            double totalRiskedCapital = p_portfolio.PortfolioUsdSize * leverage;
            var rtPrices = new Dictionary<int, PriceAndTime>() { { TickType.MID, new PriceAndTime() } };   // MID is the most honest price. LAST may happened 1 hours ago
            p_portfolio.ProposedPositions = p_portfolio.ProposedPositionSpecs.Select(r =>
            {
                int volume = 0;
                if (r.Size is FixedSize)
                    volume = (int)(r.Size as FixedSize).Size * (r.IsShort ? -1 : 1);
                else
                {
                    Contract contract = new Contract() { Symbol = r.Ticker, SecType = "STK", Currency = "USD", Exchange = "SMART" };
                    double rtPrice = 0.0;
                    StrongAssert.True(Controller.g_gatewaysWatcher.GetAlreadyStreamedPrice(contract, ref rtPrices), Severity.ThrowException, $"Warning. Realtime price for {r.Ticker} is misssing. For safety reasons, we can use volume=0 as targetVolume, but it means we would sell the current positions. Better to not continue.");
                    rtPrice = rtPrices[TickType.MID].Price;
                    volume = (int)((r.Size as WeightedSize).Weight * totalRiskedCapital / rtPrice) * (r.IsShort ? -1 : 1);
                }

                return new PortfolioPosition()
                {
                    AssetID = Strategy.TickerToAssetID(r.Ticker),
                    Volume = volume
                };
            }).ToList();

            foreach (var suggestedItem in p_portfolio.ProposedPositions)
            {
                Utils.ConsoleWriteLine(null, false, $"Portfolio suggestion: {Strategy.StockIdToTicker(suggestedItem.SubTableID)}: signed vol: {suggestedItem.Volume}");
                Utils.Logger.Info($"Portfolio suggestion: {Strategy.StockIdToTicker(suggestedItem.SubTableID)}: signed vol: {suggestedItem.Volume}");
            }
        }

        private void DetermineProposedTransactions(BrokerTaskPortfolio p_portfolio)
        {
            List<Transaction> transactions = new List<Transaction>();
            // 1. close old positions
            //var closedPositions = p_previousPidsWithoutCash.Where(r => !p_targetPortfolio.Select(q => q.AssetID).Contains(r.AssetID));
            foreach (var todayPos in p_portfolio.TodayPositions)
            {
                if (todayPos.AssetTypeID == AssetType.HardCash)
                    continue;   // skip cash positions
                //Utils.Logger.Warn($"Prev Item {Strategy.StockIdToTicker(todayPos.SubTableID)}: {todayPos.Volume} ");

                var proposed = p_portfolio.ProposedPositions.FirstOrDefault(r => r.AssetID == todayPos.AssetID);
                if (proposed == null)   // if an assetID is only in todayPos, it is here
                {
                    transactions.Add(new Transaction()
                    {
                        AssetID = todayPos.AssetID,
                        Volume = Math.Abs(todayPos.Volume),     // Volume should be always positive
                        TransactionType = (todayPos.Volume > 0) ? TransactionType.SellAsset : TransactionType.BuyAsset, // it was Cover instead of Buy, But we decided to simplify and allow Buy
                        OldVolume = todayPos.Volume
                    });
                }
                else
                {   // if an assetID is both in todayPos and in proposedPos, it is here
                    double diffVolume = proposed.Volume - todayPos.Volume; 
                    if (!Utils.IsNearZero(diffVolume))    // don't insert transaction, if volume will be 0
                    {
                        transactions.Add(new Transaction()
                        {
                            AssetID = todayPos.AssetID,
                            Volume = Math.Abs(diffVolume),     // Volume should be always positive
                            TransactionType = (diffVolume > 0) ? TransactionType.BuyAsset : TransactionType.SellAsset,
                            OldVolume = todayPos.Volume
                        });
                    }
                }
            }

            // 2. open new positions:
            foreach (var proposedPos in p_portfolio.ProposedPositions)
            {
                //Utils.Logger.Warn($"New Item {Strategy.StockIdToTicker(proposedPos.SubTableID)}: {proposedPos.Volume} ");
                var todayPos = p_portfolio.TodayPositions.FirstOrDefault(r => r.AssetID == proposedPos.AssetID);
                if (todayPos == null)   // // if an assetID is only in proposedPos, it is here
                {
                    transactions.Add(new Transaction()
                    {
                        AssetID = proposedPos.AssetID,
                        Volume = Math.Abs(proposedPos.Volume),     // Volume should be always positive
                        TransactionType = (proposedPos.Volume > 0) ? TransactionType.BuyAsset : TransactionType.SellAsset, // it was Cover instead of Buy, But we decided to simplify and allow Buy
                        OldVolume = 0
                    });
                }
            }

            foreach (var transaction in transactions)
            {
                double transactionOfOldVolumePct = (transaction.OldVolume == 0) ? 1.00 : transaction.Volume / transaction.OldVolume;
                Utils.ConsoleWriteLine(ConsoleColor.Green, false, $"***Proposed transaction: {transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume} ({transactionOfOldVolumePct*100:F2}%) ");
                Utils.Logger.Info($"***Proposed transaction: {transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume} ({transactionOfOldVolumePct*100:F2}%) ");
            }

            p_portfolio.ProposedTransactions = transactions;    // only assign at the end, if everything was right, there was no thrown Exception. It is safer to do transactions: all or nothing, not partial
        }

        private void PlaceTransactionsViaBroker(BrokerTaskPortfolio p_portfolio, StringBuilder p_detailedReportSb)
        {
            var transactions = p_portfolio.ProposedTransactions;
            if (transactions.Count == 0)
            {
                Utils.ConsoleWriteLine(ConsoleColor.Green, false, $"***Trades: none.");
                Utils.Logger.Info($"***Trades: none.");
                p_detailedReportSb.AppendLine($"***Trades: none.");
                return;
            }

            // OPG order: tried in IB: puting 2 minutes before open: result: invalid order (after 10 sec); 3 minutes before open: it was successfull
            // in theory: "Market on open orders must be placed at least 20 minutes", but IB doesn't specify the time.
            // apparently ,the exchanges close the time window 2 minutes before market open. So, if IB is quick enough, it can accept trades even in the last 5 minutes (once I tried with 3 minutes manually, and was successfull)
            // so: with IB: try: 5 minutes, and increase it 1 minute every time it failed. (at the end, we will get to 20 minutes). It may depend on the stock exchange. There are more lenient stock exchanges
            // An MOC (Market on Close) order must be  submitted no later than 15  minutes prior to the close of  the market.
            object orderExecutionObj = BrokerTaskSchema.Settings[BrokerTaskSetting.OrderExecution]; // "MKT", "LMT", "MOC"
            OrderExecution orderExecution = (orderExecutionObj != null) ? (OrderExecution)orderExecutionObj : OrderExecution.Market;    // Market is the default
            object orderTifObj = null;
            OrderTimeInForce orderTif = (BrokerTaskSchema.Settings.TryGetValue(BrokerTaskSetting.OrderTimeInForce, out orderTifObj)) ? (OrderTimeInForce)orderTifObj : OrderTimeInForce.Day;     // Day is the default
            StrongAssert.True(orderExecution == OrderExecution.Market || orderExecution == OrderExecution.MarketOnClose, Severity.ThrowException, $"Non supported OrderExecution: {orderExecution}");
            StrongAssert.True(orderTif == OrderTimeInForce.Day, Severity.ThrowException, $"Non supported OrderTimeInForce: {orderTif}");

            bool isSimulatedTrades = (bool)Trigger.TriggerSettings[BrokerTaskSetting.IsSimulatedTrades];       // simulation settings is too important to be forgotten to set. If it is not in settings, this will throw exception. OK.
            for (int i = 0; i < transactions.Count; i++)    // quickly place the orders. Don't do any other time consuming work here.
            {
                var transaction = transactions[i];
                Utils.Logger.Info($"Placing Order {transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume} ");
                Contract contract = new Contract() { Symbol = Strategy.StockIdToTicker(transaction.SubTableID), SecType = "STK", Currency = "USD", Exchange = "SMART" };
                transaction.VirtualOrderId = Controller.g_gatewaysWatcher.PlaceOrder(p_portfolio.IbGatewayUserToTrade, p_portfolio.MaxTradeValueInCurrency, p_portfolio.MinTradeValueInCurrency, contract, transaction.TransactionType, transaction.Volume, orderExecution, orderTif, null, null, isSimulatedTrades, transaction.OldVolume, p_detailedReportSb);
            } // don't do anything here. Return, so other portfolio PlaceOrder()-s can be executed too.
        }

        private bool WaitTransactionsViaBrokerAndCollectExecutionInfo(BrokerTaskPortfolio p_portfolio, StringBuilder p_errorStr)
        {
            var transactions = p_portfolio.ProposedTransactions;
            if (transactions.Count == 0)
                return true;

            bool isSimulatedTrades = (bool)Trigger.TriggerSettings[BrokerTaskSetting.IsSimulatedTrades];       // simulation settings is too important to be forgotten to set. If it is not in settings, this will throw exception. OK.
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < transactions.Count; i++)
            {
                Task task = Task.Factory.StartNew((transaction) =>
                {
                    Controller.g_gatewaysWatcher.WaitOrder(p_portfolio.IbGatewayUserToTrade, ((Transaction)transaction).VirtualOrderId, isSimulatedTrades);
                }, transactions[i]).LogUnobservedTaskExceptions("BrokerTaskTradeStrategy.WaitTransactionsViaBrokerAndCollectExecutionInfo()");
                tasks.Add(task);
            }

            //Block until all transactions complete.
            Utils.Logger.Info("Before Task.WaitAllOrders(tasks)");
            // http://stackoverflow.com/questions/9846615/async-task-whenall-with-timeout
            // Timeout here is not necessary, because BrokerWrapper.WaitOrder() will do a 2 minutes timeout for MKT orders, and Properly calculated MarketClose+2 minutes for MOC orders.
            // However, for absolute safety, to send Error Email to Supervisor, don't let an inifinte Task.WaitAll() here.
            // Imagine a case when MOC order was done at 9:30. Until 16:00, there is 6:30. So, do a 7hours timeout here.
            var allTasks = Task.WhenAll(tasks.ToArray());
            var theFirstCompletedTask = Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromHours(7))).Result;   // returns false if timeout
            Utils.Logger.Info($"After Task.WaitAllOrders(tasks). Note: BrokerWrapper.WaitOrder() will do a 2 minutes timeout (MKT, MOC). Was it 7 hours timeout: {theFirstCompletedTask != allTasks} (Good, if there was no order timeout, but it is not a guarantee that everything was right. For example. Shares were not available for shorting.");

            bool wasAnyErrorInOrders = false;
            for (int i = 0; i < transactions.Count; i++)
            {
                var transaction = transactions[i];
                OrderStatus orderStatus = OrderStatus.None; // a Property cannot be passed to a ref parameter, so we have to use temporary variables
                double executedVolume = Double.NaN;
                double executedAvgPrice = Double.NaN;
                DateTime executionTime = DateTime.UtcNow;
                if (!Controller.g_gatewaysWatcher.GetVirtualOrderExecutionInfo(p_portfolio.IbGatewayUserToTrade, transaction.VirtualOrderId, ref orderStatus, ref executedVolume, ref executedAvgPrice, ref executionTime, isSimulatedTrades))
                {
                    wasAnyErrorInOrders = true;
                    p_errorStr.AppendLine($"GetVirtualOrderExecutionInfo() failed for virtualOrderId({transaction.VirtualOrderId}): {transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume}.");
                }
                else
                {
                    transaction.OrderStatus = orderStatus;
                    transaction.ExecutedVolume = executedVolume;
                    transaction.ExecutedAvgPrice = executedAvgPrice;
                    transaction.DateTime = executionTime;
                    if (transaction.OrderStatus == OrderStatus.MinFilterSkipped)
                    {
                        Utils.Logger.Info($"Ok. {transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume}. Transaction.OrderStatus != OrderStatus.Filled. It is {transaction.OrderStatus}");       // This is Info. expected
                    }
                    else if (transaction.OrderStatus == OrderStatus.MaxFilterSkipped)
                    {
                        wasAnyErrorInOrders = true;
                        p_errorStr.AppendLine($"Error. {transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume}. Transaction.OrderStatus != OrderStatus.Filled. It is {transaction.OrderStatus}");       // This is Warn. not expected
                    }
                    else
                    {
                        if (Utils.IsNear(transaction.Volume, transaction.ExecutedVolume))
                        {
                            // everything is OK. OrderStatus should be Filled or Partially Filled.
                            if (transaction.OrderStatus != OrderStatus.Filled)
                            {
                                Utils.Logger.Warn($"{transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume}. Transaction.OrderStatus != OrderStatus.Filled. It is {transaction.OrderStatus}. Force it to be Filled.");
                                transaction.OrderStatus = OrderStatus.Filled;
                            }
                        }
                        else
                        {
                            wasAnyErrorInOrders = true;
                            p_errorStr.AppendLine($"ERROR in {transaction.TransactionType} {Strategy.StockIdToTicker(transaction.SubTableID)}: {transaction.Volume},  VirtualOrderId {transaction.VirtualOrderId}: transaction.OrderStatus != OrderStatus.Filled. It is {transaction.OrderStatus}, but Volume({transaction.Volume}) and ExecutedVolume({transaction.ExecutedVolume}) are different.");       // This is Error. not expected
                        } // else
                    }
                }   // else

            }   // for

            return !wasAnyErrorInOrders;
        }   // WaitTransactionsViaBrokerAndCollectExecutionInfo()

        private void WriteAllExecutedTransactionsToDB(List<BrokerTaskPortfolio> p_portfolios)
        {
            // insert everything where transaction.OrderStatus != OrderStatus.Filled and use ExecutedVolume
            List<DbTransaction> allOkTransactions = new List<DbTransaction>();
            foreach (var portfolio in p_portfolios)
            {
                if (portfolio.IsErrorOccured)
                    continue;
                foreach (var transaction in portfolio.ProposedTransactions)
                {
                    if (transaction.OrderStatus == OrderStatus.Filled)
                    {
                        allOkTransactions.Add(new DbTransaction()
                        {
                            PortfolioID = portfolio.PortfolioID,
                            TransactionType = transaction.TransactionType,
                            AssetID = transaction.AssetID,
                            Volume = transaction.ExecutedVolume,
                            Price = transaction.ExecutedAvgPrice,
                            DateTime = transaction.DateTime,
                            Note = null
                        });
                    }
                }
            }

            bool isSimulatedTrades = (bool)Trigger.TriggerSettings[BrokerTaskSetting.IsSimulatedTrades];       // simulation settings is too important to be forgotten to set. If it is not in settings, this will throw exception. OK.
            if (isSimulatedTrades)
                return;
            bool isInsertsOk = SqlTools.InsertTransactionsToDB(allOkTransactions).Result;
            Utils.Logger.Info($"SQL inserts OK: {isInsertsOk}");

        }
    }   // class
} // namespace
