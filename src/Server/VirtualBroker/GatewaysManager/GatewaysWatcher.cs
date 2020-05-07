using DbCommon;
using IBApi;
using SqCommon;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Utils = SqCommon.Utils;

namespace VirtualBroker
{
    public class SavedState : PersistedState   // data to persist between restarts of the crawler process
    {
        public bool IsSendErrorEmailAtGracefulShutdown { get; set; } = true;   // switch this off before deployment, and switch it on after deployment; make functionality on the WebSite
    }

    
    
    public partial class GatewaysWatcher
    {
        const double cReconnectTimerFrequencyMinutes = 15; 
        System.Threading.Timer m_reconnectTimer = null;
        SavedState m_persistedState = null;
        List<Gateway> m_gateways = new List<Gateway>();

        GatewayUser m_mainGatewayUser;
        Gateway m_mainGateway = null;

        bool m_isSupportPreStreamRealtimePrices;        
        public SavedState PersistedState
        {
            get
            {
                return m_persistedState;
            }

            set
            {
                m_persistedState = value;
            }
        }

        public void Init()
        {
            Utils.Logger.Info("***GatewaysWatcher:Init()");
            PersistedState = new SavedState();

            string isSupportPreStreamRealtimePricesStr = Utils.Configuration["SupportPreStreamRealtimePrices"];
            Console.WriteLine($"SupportPreStreamRealtimePrices: {isSupportPreStreamRealtimePricesStr ?? "False"}");
            m_isSupportPreStreamRealtimePrices = isSupportPreStreamRealtimePricesStr != null && isSupportPreStreamRealtimePricesStr.ToUpper() == "TRUE";

            Gateway gateway1 = null, gateway2 = null, gateway3 = null;
            if (Controller.IsRunningAsLocalDevelopment())
            {
                gateway1 = new Gateway(GatewayUser.GyantalMain, p_accountMaxTradeValueInCurrency: 100000 /* UberVXX is 12K, 2xleveraged=24K, double=48K*/, p_accountMaxEstimatedValueSumRecentlyAllowed: 160000) { VbAccountsList = "U407941", SocketPort = (int)GatewayUserPort.GyantalMain, BrokerConnectionClientID = 41 };
                m_mainGatewayUser = GatewayUser.GyantalMain;
            }
            else
            {
                // either VBroker server connecting to IbGateways (Agy, Charmat, Tu) or ManualTradingServer connecting to TWS (Charmat, DeBlanzac)
                var vbServerEnvironment = Utils.Configuration["VbServerEnvironment"];
                Console.WriteLine($"VbServerEnvironment: {vbServerEnvironment ?? "NULL"}");
                StrongAssert.True(vbServerEnvironment != null, Severity.Halt, "Configuration['VbServerEnvironment'] is missing. It is safer to terminate.");
                if (vbServerEnvironment.ToLower() == "AutoTradingServer".ToLower())
                {
                    gateway1 = new Gateway(GatewayUser.GyantalMain, p_accountMaxTradeValueInCurrency: 100000 /* UberVXX is 12K, 2xleveraged=24K, double=48K*/, p_accountMaxEstimatedValueSumRecentlyAllowed: 160000) { VbAccountsList = "U407941", SocketPort = (int)GatewayUserPort.GyantalMain, BrokerConnectionClientID = 41 };
                    gateway2 = new Gateway(GatewayUser.CharmatSecondary, p_accountMaxTradeValueInCurrency: 600000.0 /* HarryLong is played 400K*70%=300K, double it */, p_accountMaxEstimatedValueSumRecentlyAllowed: 1000000  /* 1M */ ) { VbAccountsList = "U988767", SocketPort = (int)GatewayUserPort.CharmatSecondary, BrokerConnectionClientID = 42 };
                    //gateway3 = new Gateway(GatewayUser.TuSecondary, p_accountMaxTradeValueInCurrency: 15000.0 /* HarryLong is played 10K*70%=7K, double it */, p_accountMaxEstimatedValueSumRecentlyAllowed: 20000  /* 20K */ ) { VbAccountsList = "U1156489", SocketPort = (int)GatewayUserPort.TuSecondary, BrokerConnectionClientID = 43 };
                    //Gateway gateway2 = new Gateway() { GatewayUser = GatewayUser.CharmatWifeMain, VbAccountsList = "U1034066", SocketPort = 7302 };
                    m_mainGatewayUser = GatewayUser.CharmatSecondary;
                }
                else if (vbServerEnvironment.ToLower() == "ManualTradingServer".ToLower())
                {
                    gateway1 = new Gateway(GatewayUser.CharmatMain, p_accountMaxTradeValueInCurrency: 1.0 /* don't trade here */, p_accountMaxEstimatedValueSumRecentlyAllowed: 10) { VbAccountsList = "U988767", SocketPort = (int)GatewayUserPort.CharmatMain, BrokerConnectionClientID = 142 };
                    gateway2 = new Gateway(GatewayUser.DeBlanzacMain, p_accountMaxTradeValueInCurrency: 1.0 /* don't trade here */, p_accountMaxEstimatedValueSumRecentlyAllowed: 10) { VbAccountsList = "U1146158", SocketPort = (int)GatewayUserPort.DeBlanzacMain, BrokerConnectionClientID = 144 };

                    m_mainGatewayUser = GatewayUser.CharmatMain;
                }
                else
                    StrongAssert.Fail(Severity.Halt, "Configuration['VbServerEnvironment'] is not recognized. It is safer to terminate.");
            }

            m_gateways = new List<Gateway>() { gateway1 };   // delete previous Gateway connections
            if (gateway2 != null)   // sometimes (for development), 1 gateway is used only
                m_gateways.Add(gateway2);
            if (gateway3 != null)   // sometimes (for development), 1 gateway is used only
                m_gateways.Add(gateway3);

            m_mainGateway = m_gateways.Find(r => r.GatewayUser == m_mainGatewayUser);

            m_reconnectTimer = new System.Threading.Timer(new TimerCallback(ReconnectToGatewaysTimer_Elapsed), null, TimeSpan.Zero, TimeSpan.FromMinutes(cReconnectTimerFrequencyMinutes));
        }

        private void ReconnectToGatewaysTimer_Elapsed(object p_stateObj)   // Timer is coming on a ThreadPool thread
        {
            Utils.Logger.Info("GatewaysWatcher:ReconnectToGatewaysTimer_Elapsed() BEGIN");
            try
            {
                bool isMainGatewayConnectedBefore = m_mainGateway.IsConnected;
                //Task connectTask1 = Task.Factory.StartNew(ReconnectToGateway, gateway1, TaskCreationOptions.LongRunning);
                //Task connectTask2 = Task.Factory.StartNew(ReconnectToGateway, gateway2, TaskCreationOptions.LongRunning);
                var reconnectTasks = m_gateways.Where(l=> !l.IsConnected).Select(r => Task.Factory.StartNew(ReconnectToGateway, r, TaskCreationOptions.LongRunning).LogUnobservedTaskExceptions("GatewaysWatcher.reconnectTasks"));

                // At the beginning: Linux had a problem to Connect sequentially on 2 separate threads. Maybe Linux DotNetCore 'Beta' implementation synchronization problem. Temporary connect sequentally. maybe doing Connection sequentially, not parallel would help
                foreach (var task in reconnectTasks)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(1));  // just for synch safety. However, if Linux has synch problem, we can have problems at order execution...!!
                    task.Wait();
                }
                //await Task.WhenAll(reconnectTasks); // async. This threadpool thread will return to the threadpool for temporary reuse, and when tasks are ready, it will be recallade
                ////Task.WaitAll(connectTask1, connectTask2);     // blocking wait. This thread will wait forever if needed, but we don't want to starve the threadpool
                Utils.Logger.Info("GatewaysWatcher:ReconnectToGateways() reconnectTasks ended.");

                foreach (var gateway in m_gateways)
                {
                    Utils.Logger.Info($"GatewayUser: '{gateway.GatewayUser}' IsConnected: {gateway.IsConnected}");
                }
                if (!isMainGatewayConnectedBefore && m_mainGateway.IsConnected)   // if this is the first time mainGateway connected after being dead
                    MainGatewayJustConnected();
            }
            catch (Exception e)
            {
                Utils.Logger.Info("GatewaysWatcher:TryReconnectToGateways() in catching exception (it is expected on MTS that TWS is not running, so it cannot connect): " + e.ToStringWithShortenedStackTrace(400));
            }

            var vbServerEnvironment = Utils.Configuration["VbServerEnvironment"];
            if (vbServerEnvironment.ToLower() == "AutoTradingServer".ToLower())
            {
                // Without all the IB connections (isAllConnected), we can choose to crash the App, but we do NOT do that, because we may be able to recover them later. 
                // It is a strategic (safety vs. conveniency) decision: in that case if not all IBGW is connected, (it can be an 'expected error'), VBroker runs further and try connecting every 10 min.
                // on ManualTrader server failed connection is expected. Don't send Error. However, on AutoTraderServer, it is unexpected (at the moment), because IBGateways and VBrokers restarts every day.
                var notConnectedGateways = String.Join(",", m_gateways.Where(l => !l.IsConnected).Select(r => r.GatewayUser + "/"));
                if (!String.IsNullOrEmpty(notConnectedGateways)) {
                     if (!IsApproximatelyMarketTradingTimeForIgnoringIBErrors(true))
                        return; // skip processing the error further. Don't send it to HealthMonitor.
                    HealthMonitorMessage.SendAsync($"Gateways are not connected. vbServerEnvironment: '{vbServerEnvironment}', not connected gateways {notConnectedGateways}", HealthMonitorMessageID.ReportErrorFromVirtualBroker).ConfigureAwait(continueOnCapturedContext: false);
                }
            }
            Utils.Logger.Info("GatewaysWatcher:ReconnectToGatewaysTimer_Elapsed() END");
        }

        void ReconnectToGateway(object p_object)
        {
            Gateway gateway = (Gateway)p_object;
            try
            {
                gateway.Reconnect();
            }
            catch (System.Exception)
            {
                // swallow the exception. we can choose not to crash the App, but repeat connection later
            }
        }

        private void MainGatewayJustConnected()
        {
            if (m_isSupportPreStreamRealtimePrices)
            {
                // getting prices of SPY (has dividend, but liquid) or VXX (no dividend, but less liquids) is always a must. An Agent would always look that price. So, subscribe to that on the MainGateway
                // see what is possible to call: "g:\temp\_programmingTemp\TWS API_972.12(2016-02-26)\samples\CSharp\IBSamples\IBSamples.sln" 

                // for NeuralSniffer
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("^RUT"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("UWM"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("TWM"));

                // for UberVXX
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("VXX"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("SVXY"));
                //m_mainGateway.BrokerWrapper.ReqMktDataStream(new Contract() { Symbol = "SPY", SecType = "STK", Currency = "USD", Exchange = "SMART" }); // for TotM forecast, but it is not needed just yet

                // for HarryLong
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("TQQQ"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("ZIV"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("VXZ"));

                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("TMV"));
                
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("SCO"));  // 2020-04-02: use SCO (2x); instead of short USO (1x), short UWT (-3x) was used, but it was delisted, because it went to penny stock

                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("UGAZ")); // instead of UNG for DC
                
                // m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("TMF")); // needed until 2018-05-01, when this will be change back to TMV for Agy. However, TMV is not shortable for a long time.
                // m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("UNG"));  // NatGas (1x) for Agy, but this can be commented out in general.

                // for TAA, but it is only temporary. We will not stream this unnecessary data all day long, as TAA can take its time. It only trades MOC. Extra 2-3 seconds doesn't matter.
                // "TLT"+ "MDY","ILF","FEZ","EEM","EPP","VNQ","IBB"  +  "MVV", "URE", "BIB"
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("TLT"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("MDY"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("ILF"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("FEZ"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("EEM"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("EPP"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("VNQ"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("IBB"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("MVV"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("URE"));
                m_mainGateway.BrokerWrapper.ReqMktDataStream(VBrokerUtils.ParseSqTickerToContract("BIB"));
            }
        }

       
        // at graceful shutdown, it is called
        public void Exit()
        {
            foreach (var gateway in m_gateways)
            {
                gateway.Disconnect();
            }

            //PersistedState.Save();
            //StopTcpMessageListener();
        }

        // there are some weird IB errors that happen usually when IB server is down. 99% of the time it is at the weekend, or when pre or aftermarket. In this exceptional times, ignore errors.
        public static bool IsApproximatelyMarketTradingTimeForIgnoringIBErrors(bool p_isIbNotConnectedError = false)
        {
            DateTime utcNow = DateTime.UtcNow;
            DateTime etNow = Utils.ConvertTimeFromUtcToEt(utcNow);
            if (etNow.DayOfWeek == DayOfWeek.Saturday || etNow.DayOfWeek == DayOfWeek.Sunday)   // if it is the weekend => no Error
                return false;

            // The NYSE and NYSE MKT are open from Monday through Friday 9:30 a.m. to 4:00 p.m. ET.
            TimeSpan timeTodayEt = etNow - etNow.Date;
            if (p_isIbNotConnectedError)  // "Gateways are not connected" errors handled with more strictness. We expect that there is a connection to IBGateway at least 1 hour before open. At 8:30.
            {
                if (timeTodayEt.TotalMinutes < 8 * 60 + 29)
                    return false;   // if it is not Approximately around market hours => no Error
            }
            else
            {
                if (timeTodayEt.TotalMinutes < 9 * 60 + 29)
                    return false;   // if it is not Approximately around market hours => no Error
            }

            if (timeTodayEt.TotalMinutes > 17 * 60)
                return false;   // if it is not Approximately around market hours => no Error

            // you can skip holiday days too later
            return true;
        }

        internal bool IsGatewayConnected(GatewayUser p_ibGatewayUserToTrade)
        {
            var gateway = m_gateways.FirstOrDefault(r => r.GatewayUser == p_ibGatewayUserToTrade);
            if (gateway == null)
                return false;
            return (gateway.IsConnected);
        }


        internal bool GetAlreadyStreamedPrice(Contract p_contract, ref Dictionary<int, PriceAndTime> p_quotes)
        {
            if (m_mainGateway == null || !m_mainGateway.IsConnected)
                return false;
            return m_mainGateway.BrokerWrapper.GetAlreadyStreamedPrice(p_contract, ref p_quotes);
        }

        internal bool ReqHistoricalData(DateTime p_endDateTime, int p_lookbackWindowSize, string p_whatToShow, Contract p_contract, out List<QuoteData> p_quotes)
        {
            p_quotes = null;
            if (m_mainGateway == null || !m_mainGateway.IsConnected)
                return false;
            return m_mainGateway.BrokerWrapper.ReqHistoricalData(p_endDateTime, p_lookbackWindowSize, p_whatToShow, p_contract, out p_quotes);
        }

        internal int PlaceOrder(GatewayUser p_gatewayUserToTrade, double p_portfolioMaxTradeValueInCurrency, double p_portfolioMinTradeValueInCurrency, 
            Contract p_contract, TransactionType p_transactionType, double p_volume, OrderExecution p_orderExecution, OrderTimeInForce p_orderTif, double? p_limitPrice, double? p_stopPrice, bool p_isSimulatedTrades, double p_oldVolume, StringBuilder p_detailedReportSb)
        {
            Gateway userGateway = m_gateways.FirstOrDefault(r => r.GatewayUser == p_gatewayUserToTrade);
            if (userGateway == null || !userGateway.IsConnected)
            {
                Utils.Logger.Error($"ERROR. PlacingOrder(). GatewayUserToTrade {p_gatewayUserToTrade} is not found among connected Gateways or it is not connected.");
                return -1;
            }

            var rtPrices = new Dictionary<int, PriceAndTime>() { { TickType.MID, new PriceAndTime() } };   // MID is the most honest price. LAST may happened 1 hours ago
            m_mainGateway.BrokerWrapper.GetAlreadyStreamedPrice(p_contract, ref rtPrices);
            int virtualOrderId = userGateway.PlaceOrder(p_portfolioMaxTradeValueInCurrency, p_portfolioMinTradeValueInCurrency, p_contract, p_transactionType, p_volume, p_orderExecution, p_orderTif, p_limitPrice, p_stopPrice, rtPrices[TickType.MID].Price, p_isSimulatedTrades, p_oldVolume, p_detailedReportSb);
            return virtualOrderId;
        }

        internal bool WaitOrder(GatewayUser p_gatewayUserToTrade, int p_virtualOrderId, bool p_isSimulatedTrades)
        {
            Gateway userGateway = m_gateways.FirstOrDefault(r => r.GatewayUser == p_gatewayUserToTrade);
            if (userGateway == null || !userGateway.IsConnected)
                return false;

            return userGateway.WaitOrder(p_virtualOrderId, p_isSimulatedTrades);
        }

        internal bool GetVirtualOrderExecutionInfo(GatewayUser p_gatewayUserToTrade, int p_virtualOrderId, ref OrderStatus orderStatus, ref double executedVolume, ref double executedAvgPrice, ref DateTime executionTime, bool p_isSimulatedTrades)
        {
             Gateway userGateway = m_gateways.FirstOrDefault(r => r.GatewayUser == p_gatewayUserToTrade);
            if (userGateway == null)
                return false;

            return userGateway.GetVirtualOrderExecutionInfo(p_virtualOrderId, ref orderStatus, ref executedVolume, ref executedAvgPrice, ref executionTime, p_isSimulatedTrades);
        }

        public string GetRealtimePriceService(string p_query)
        {
            if (m_mainGateway == null || !m_mainGateway.IsConnected)
                return null;
            return m_mainGateway.GetRealtimePriceService(p_query);
        }

        
    }
}
