using DbCommon;
using IBApi;
using SqCommon;
using SQCommon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;     // this is the only timer available under DotNetCore
using System.Threading.Tasks;

namespace VirtualBroker
{
    public partial class Controller
    {
        DateTime m_startTime;

        // it is better to not store Global State, for example TickerProvider, because it can go stale (the cached copy is not up-to-date), and reqular updating is a lot of problem: when to do it, every 10 minutes or every day?
        // so, don't store State, and cache. When required, fetch it from the SQL server 5 seconds before its is needed
        //internal static DBManager g_dbManager = new DBManager();  // don't cache dbManager state. Max. cache the SqlConnections

        public static Controller g_controller = new Controller();
        public static GatewaysWatcher g_gatewaysWatcher = new GatewaysWatcher();    // this is the Trading Risk Manager Agent. The gateway for trading.
        public static BrokerScheduler g_brokerScheduler = new BrokerScheduler();    // this is the Boss of the Virtual Broker bees. It schedules them.
        public static List<BrokerTaskSchema> g_taskSchemas = new List<BrokerTaskSchema>();  // the worker bees, the Trading Agents
        

        internal void Init()
        {
            Utils.Logger.Info("****VBroker:Init()");
            m_startTime = DateTime.UtcNow;

            g_gatewaysWatcher.Init();
            BuildTasks();
            g_brokerScheduler.Init();

            m_tcpListener = new ParallelTcpListener(VirtualBrokerMessage.VirtualBrokerServerPrivateIpForListener, VirtualBrokerMessage.DefaultVirtualBrokerServerPort, ProcessTcpClient);
            Console.WriteLine($"VBroker is listening on privateIP: {VirtualBrokerMessage.VirtualBrokerServerPrivateIpForListener}:{VirtualBrokerMessage.DefaultVirtualBrokerServerPort}");
            m_tcpListener.StartTcpMessageListenerThreads();
        }

        public static bool IsRunningAsLocalDevelopment()
        {
            if (Utils.RunningPlatform() == Platform.Linux)    // assuming production environment on Linux, Other ways to customize: ifdef DEBUG/RELEASE  ifdef PRODUCTION/DEVELOPMENT, etc. this Linux/Windows is fine for now
            {
                return false;
            }
            else
            {
                // Windows: however, sometimes, when Running on Windows, we want to Run it as a proper Production environment. E.g.
                //      + Sometimes, for Debugging reasons, 
                //      + sometimes, because Linux server is down and running the Production locally on Windows
                return true;
            }
        }

        public void BuildTasks()
        {
            var isSupportBrokerTasksStr = Utils.Configuration["SupportBrokerTasks"];
            Console.WriteLine($"SupportBrokerTasks: {isSupportBrokerTasksStr ?? "False"}");
            if (isSupportBrokerTasksStr == null)
                return;
            if (isSupportBrokerTasksStr.ToUpper() != "TRUE")
                return;

            // IB MOC orders: https://www.interactivebrokers.com/en/index.php?f=599  (NYSE, NYSE MKT, NYSE Arca: 15:45 ET, Nasdaq: 15:50 ET)
            // EU PRIIPs Regulation (2018-06-19) : EU based investors cannot trade SPY, QQQ, etc. ETFs, only the UCITS equivalents in EU, but they are scarce. Reverting to manual trading for a while.
            //var neuralSniffer1TaskSchema = new BrokerTaskSchema()
            //{
            //    Name = "NeuralSniffer1",
            //    BrokerTaskFactory = BrokerTaskTradeStrategy.BrokerTaskFactoryCreate,
            //    Settings = new Dictionary<object, object>() {  //not necessary, because VBrokerTask can have local parameters inside itself
            //        { BrokerTaskSetting.StrategyFactory, new Func<IBrokerStrategy>(NeuralSniffer1Strategy.StrategyFactoryCreate) },
            //        { BrokerTaskSetting.OrderExecution, OrderExecution.MarketOnClose },
            //        { BrokerTaskSetting.Portfolios, new List<BrokerTaskPortfolio>()
            //            {
            //            new BrokerTaskPortfolio() { Name = "! NeuralSniffer Aggressive Agy Live", HQUserID = HQUserID.gyantal, IbGatewayUserToTrade = GatewayUser.GyantalMain,
            //                Param = new PortfolioParamNeuralSniffer1() { PlayingInstrumentUpsideLeverage = -2.0, PlayingInstrumentDownsideLeverage = -2.0 } }
            //            }
            //        }
            //    }
            //};
            //neuralSniffer1TaskSchema.Triggers.Add(new VbTrigger()
            //{
            //    TriggeredTaskSchema = neuralSniffer1TaskSchema,
            //    TriggerType = TriggerType.DailyOnUsaMarketDay,
            //    StartTimeBase = StartTimeBase.BaseOnUsaMarketOpen,
            //    StartTimeOffset = TimeSpan.FromMinutes(15),
            //    TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            //});
            //neuralSniffer1TaskSchema.Triggers.Add(new VbTrigger()
            //{
            //    TriggeredTaskSchema = neuralSniffer1TaskSchema,
            //    TriggerType = TriggerType.DailyOnUsaMarketDay,
            //    StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
            //    StartTimeOffset = TimeSpan.FromMinutes(-43),
            //    TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            //});
            //neuralSniffer1TaskSchema.Triggers.Add(new VbTrigger()
            //{
            //    TriggeredTaskSchema = neuralSniffer1TaskSchema,
            //    TriggerType = TriggerType.DailyOnUsaMarketDay,
            //    StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
            //    StartTimeOffset = TimeSpan.FromMinutes(-17),
            //    TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, false } }
            //});
            //g_taskSchemas.Add(neuralSniffer1TaskSchema);


            //var taaTaskSchema = new BrokerTaskSchema()
            //{
            //    Name = "TAA",
            //    BrokerTaskFactory = BrokerTaskTradeStrategy.BrokerTaskFactoryCreate,
            //    Settings = new Dictionary<object, object>() {  //not necessary, because VBrokerTask can have local parameters inside itself
            //        { BrokerTaskSetting.StrategyFactory, new Func<IBrokerStrategy>(TAAStrategy.StrategyFactoryCreate) },
            //        { BrokerTaskSetting.OrderExecution, OrderExecution.MarketOnClose },
            //        { BrokerTaskSetting.Portfolios, new List<BrokerTaskPortfolio>()
            //            {
            //            new BrokerTaskPortfolio() { Name = "! Advanced UberTAA with Global Assets Agy Live", HQUserID = HQUserID.gyantal, IbGatewayUserToTrade = GatewayUser.GyantalMain,
            //                MaxTradeValueInCurrency = 15000, // portfolio is 10K original, but it has 7assets+TLT in it.  If everything is Cash (not likely), TLT is used as 10K. So, set it up as 15K just to think about the future.
            //                MinTradeValueInCurrency = 100,
            //                Param = new PortfolioParamTAA() { } }
            //            }
            //        }
            //    }
            //};
            //taaTaskSchema.Triggers.Add(new VbTrigger()
            //{
            //    TriggeredTaskSchema = taaTaskSchema,
            //    TriggerType = TriggerType.DailyOnUsaMarketDay,
            //    StartTimeBase = StartTimeBase.BaseOnUsaMarketOpen,
            //    StartTimeOffset = TimeSpan.FromMinutes(20),
            //    TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            //});
            //taaTaskSchema.Triggers.Add(new VbTrigger()
            //{
            //    TriggeredTaskSchema = taaTaskSchema,
            //    TriggerType = TriggerType.DailyOnUsaMarketDay,
            //    StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
            //    StartTimeOffset = TimeSpan.FromMinutes(-39),
            //    TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            //});
            //taaTaskSchema.Triggers.Add(new VbTrigger()
            //{
            //    TriggeredTaskSchema = taaTaskSchema,
            //    TriggerType = TriggerType.DailyOnUsaMarketDay,
            //    StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
            //    StartTimeOffset = TimeSpan.FromMinutes(-16),
            //    TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, false } }
            //});
            //g_taskSchemas.Add(taaTaskSchema);


            var uberVxxTaskSchema = new BrokerTaskSchema()
            {
                Name = "UberVXX",
                BrokerTaskFactory = BrokerTaskTradeStrategy.BrokerTaskFactoryCreate,
                Settings = new Dictionary<object, object>() {  //not necessary, because VBrokerTask can have local parameters inside itself
                    { BrokerTaskSetting.StrategyFactory, new Func<IBrokerStrategy>(UberVxxStrategy.StrategyFactoryCreate)  },
                    { BrokerTaskSetting.OrderExecution, OrderExecution.Market },
                    { BrokerTaskSetting.Portfolios, new List<BrokerTaskPortfolio>()
                        {
                        //new BrokerTaskPortfolio() { Name = "! AdaptiveConnor,VXX autocorrelation (VXX-XIV, stocks, noHedge) Agy Live", HQUserID = HQUserID.gyantal, IbGatewayUserToTrade = GatewayUser.GyantalMain,
                        //    MaxTradeValueInCurrency = 48000, // portfolio is 5K original, 8K is 2017-01, but it plays double leverage: 16K-20K. 20K is possible. So, almost double the range to 35K too.
                        //    MinTradeValueInCurrency = 100,
                        //    //Param = new PortfolioParamUberVXX() { PlayingInstrumentVixLongLeverage = 1.0, PlayingInstrumentVixShortLeverage = 2.0 } },
                        //    //Param = new PortfolioParamUberVXX() { PlayingInstrumentVixLongLeverage = 1.0, PlayingInstrumentVixShortLeverage = 1.0 } },  // 2017-08-14: "VixShortLeverage" from 2.0 to 1.0, because InitialMargin of 'long SVXY' is 100%, and 'short TVIX' is 300%
                        //    //Param = new PortfolioParamUberVXX() { PlayingInstrumentVixLongLeverage = 1.0, PlayingInstrumentVixShortLeverage = 2.0 } },  // 2017-08-22: "VixShortLeverage" from 1.0 to 2.0, because InitialMargin of 'long SVXY' has OK margin now. 'Portfolio Margin Details' shows about 43% margin for ZIV, and something similar to XIV. Margins are moderated.
                        //    //Param = new PortfolioParamUberVXX() { PlayingInstrumentVixLongLeverage = 1.0, PlayingInstrumentVixShortLeverage = 1.0 } },  // 2017-08-14: "VixShortLeverage" from 2.0 to 1.0 again, because IB: "assuming a decline of 10% in the market index SPX and a consistent increase in the market volatility index VIX. With VIX currently around 11 the consistent upward move will put VIX around 37. At higher levels of VIX, the VIX change corresponding to a 10% decline in the SPX will be lower (in accordance with expected behavior of the beta between VIX and SPX)."
                        //    //Param = new PortfolioParamUberVXX() { PlayingInstrumentVixLongLeverage = 1.0, PlayingInstrumentVixShortLeverage = 2.0 } },    // 2017-11-16: back to normal as we have 29K available funds now
                        //    Param = new PortfolioParamUberVXX() { PlayingInstrumentVixLongLeverage = 1.0, PlayingInstrumentVixShortLeverage = 1.5 } },    // 20178-01-03: After having 100+% in 2017, prefer safer play now. IB margin handling is still bad. 100% maintenance margin and 110% initial margin. Calculated from yesterday closePrice. In case of -20% intraday VXX spike, these margin can be reached. Only have 15K available funds. On top of it: realized that in case of XIV termination event, portfolio can lose more than its value. Not losing 100% and going to Zero, but losing an extra -100%. That shouldn't be allowed. In 2018, let's play safer and only 150% shorts, not 200%.
                        new BrokerTaskPortfolio() { Name = "! AdaptiveConnor,VXX autocorrelation (VXX-XIV, stocks, noHedge) Live", HQUserID = HQUserID.drcharmat, IbGatewayUserToTrade = GatewayUser.CharmatSecondary,
                            MaxTradeValueInCurrency = 40000, // >For DC (10K original, 13K now, I would set MaxValue=40K (assuming portfolio double in a year)
                            MinTradeValueInCurrency = 100,
                            Param = new PortfolioParamUberVXX() { PlayingInstrumentVixLongLeverage = 1.0, PlayingInstrumentVixShortLeverage = 1.0 } }
                        }
                    }
                }
            };
            uberVxxTaskSchema.Triggers.Add(new VbTrigger()
            {
                TriggeredTaskSchema = uberVxxTaskSchema,
                TriggerType = TriggerType.DailyOnUsaMarketDay,
                StartTimeBase = StartTimeBase.BaseOnUsaMarketOpen,
                StartTimeOffset = TimeSpan.FromMinutes(25),
                TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            });
            uberVxxTaskSchema.Triggers.Add(new VbTrigger()
            {
                TriggeredTaskSchema = uberVxxTaskSchema,
                TriggerType = TriggerType.DailyOnUsaMarketDay,
                StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
                StartTimeOffset = TimeSpan.FromMinutes(-35),
                TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            });
            uberVxxTaskSchema.Triggers.Add(new VbTrigger()
            {
                TriggeredTaskSchema = uberVxxTaskSchema,
                TriggerType = TriggerType.DailyOnUsaMarketDay,
                StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
                StartTimeOffset = TimeSpan.FromSeconds(-15),    // from -20sec to -15sec. From start, the trade executes in 2seconds
                TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, false } }
            });
            g_taskSchemas.Add(uberVxxTaskSchema);





            var harryLongTaskSchema = new BrokerTaskSchema()
            {
                Name = "HarryLong",
                BrokerTaskFactory = BrokerTaskTradeStrategy.BrokerTaskFactoryCreate,
                Settings = new Dictionary<object, object>() {  //not necessary, because VBrokerTask can have local parameters inside itself
                    { BrokerTaskSetting.StrategyFactory, new Func<IBrokerStrategy>(HarryLongStrategy.StrategyFactoryCreate) },
                    { BrokerTaskSetting.OrderExecution, OrderExecution.Market },
                    { BrokerTaskSetting.Portfolios, new List<BrokerTaskPortfolio>()
                        {
                        // new BrokerTaskPortfolio() { Name = "! HarryLong2(Contango-Bond) harvester Agy Live", HQUserID = HQUserID.gyantal, IbGatewayUserToTrade = GatewayUser.GyantalMain,
                        //    MaxTradeValueInCurrency = 110000, // For Agy: portfolio is 50K original. Set MaxValue=40K  (HarryLong shouldn't trade more than that, because it is only a small adjustment every day)
                        //    MinTradeValueInCurrency = 100,
                        //    Param = new PortfolioParamHarryLong() {
                        //        // 2018-03-29: for tax reasons, change TMV to TMF, and ZIV to VXZ for 30 days. Then change it back.
                        //        //Tickers = new string[] { "SVXY", "VXX", "VXZ", "TQQQ", "TMF", "UWT", "UNG" }, AssetsWeights = new double[] { 0.15, -0.05, -0.10, 0.25, 0.85, -0.09, -0.78 }    // 78% UNG is the official QuickTester weight. MaxRisked 227% of the PV. overleveraged.
                        //        //Tickers = new string[] { "SVXY", "VXX", "VXZ", "TQQQ", "TMF", "UWT", "UNG" }, AssetsWeights = new double[] { 0.15, -0.05, -0.10, 0.30, 0.90, -0.09, -0.48 }   // 2019-02: take away 30% unleveraged from UNG (because it was risky as I have to short UNG, cannot long a short insrument), and adding 10% to QQQ, which is 3.3% TQQQ, and 20% to TLT, which is 7% TMF; this decreased the CAGR by 3%, but increased Sharpe from 1.04 to 1.11 and gives better maxDD too. It seems NatGas is too volatile (as I experienced it when I shorted UNG). MaxRisked 207% of the PV.
                        //        //Tickers = new string[] { "SVXY", "VXX", "VXZ", "TQQQ", "TMF", "SCO", "UNG" }, AssetsWeights = new double[] { 0.15, -0.05, -0.10, 0.30, 0.90, 0.14, -0.48 }   // 2019-02:  MaxRisked 212% of the PV., OIL: short UWT (3x) doesn't have options.  Also, we want long position. Long SCO (2x) can be used instead. Agy: even the 48% UNG I find it quite risky (if it doubles). In practice, I try to keep this at 41% level, instead of 48%
                        //        Tickers = new string[] { "SVXY", "VXX", "VXZ", "TQQQ", "TMF", "SCO", "UNG" }, AssetsWeights = new double[] { 0.15*0.85, -0.05*0.85, -0.10*0.85, 0.30*0.85, 0.90*0.85, 0.14*0.85, -0.48*0.85 }   // 2020-03-18:  MaxRisked 212%*0.90=1.90 of the PV, 212%*0.85=1.80, in 2 years, PV grew from $50K to $150K. While the whole IB account is 210K. Leverage should be decreased, because with a $150K PV, it is dangerous to hold exposure >300K, while there are other positions in the account.
                        //    // >2020-03-18 market panic: the practical lesson for the future: Do daily rebalancing, even manually (VBroker can send an email with suggestion). Do NOT overleverage it! Even rebalance it daily. 
                        //    // Keep Risked exposure leverage under x1.9 above 100K PV, <=1.8 above 150K PV (** we are here Now **), <=1.7 above 200K PV, <=1.6 above 300K PV, <=1.5 above 400K PV, <=1.4 above 1M, <=1.3 above 2M, <=1.2 above 4M
                        //    // don't trade anything manually under 2% (which is 1-2K)
                        //    // When TLT reaches 180, its max, then TLT will stop being a good hedge. As it cannot increase in case of panic. That time, or when 10-year yield is around 0.3%, then decrease TLT weight, and increase other hedges: more UNG, SCO. Maybe introduce copper or short EEM (a bit).

                        //     // >Should we replace whole VXX to SVXY ? Not. Don't change it. 
                        //     // >It is tempting, because in 2020 VXX went from 13 to 80 (6x) within a month, so it can be 10x, and it is a disaster if not rebalanced daily.
                        //     // The reason to not decrease VXX to 0, in 2018 Volmageddon, XIV was terminated when VIX futures and VXX went up more than +100% aftermarket for a second, while short VXX survived perfectly. That is why we introduced both VXX, + SVXY.
                        //     // >Maybe decrease VXX to 2% or 0%, and add +4% to SVXY? 
                        //     // currently VXX is 5%, but it can be x10, increase 50%, and it is difficult to manage. Maybe decrease it to 2% or 0%, and add +4% to SVXY. That is much less manual rebalancing in the future. Maybe, 10% VXZ should be replaced by ZIV too, for similar reason. VXZ tripled from 15 to 40.
                        //     // The reason to not decrease VXX to 0, because last time, XIV was terminated when in went down more than -100% aftermarket, while short VXX survived. 
                        //     // So, last time actually XIV was the wrong vehicle. That is why we introduced both VXX, + SVXY. Althouh SVXY is only 50% leveraged, so much safer than XIV was.
                        //     // However, if there is no daily rebalancing, shortVXX can go up from 13 to 80 (it was 6x),but it can go up x10. So, a 5% can grow to 50% (which wit just one doubling can wipe out -100%). So even 5% is dangerous, IF it is not rebalanced daily. 
                        //     // >Maybe change VXX to SVXY only until there is daily rebalancing. And then change back to dual VXX,SVXY mode. 
                        //     // >Actually, we want to restore the semi-automatic daily rebalancing. So, this will be halfway handled automatically. So, keep both SVXY + VXX (5%) and keep VXZ at the moment, and we will see how semi-automatic rebalancing will work.
                        //    } },
                        // 2018-02-06: when VIX went to 50 in market panic, XIV was terminated, I thought it is better to retire this for DC. 200K portfolio ended in 130K. About -70K loss. He wouldn't like to continue that.
                        // 2018-03-28: we restarted HL. PV was 135K, but restarted with 150K. However, HL made safer, because we halved all weights. CAGR: 60% to 31%; maxDD: -53% to -30%. Sharpe: 1.17 to 1.20. Good.
                        new BrokerTaskPortfolio() { Name = "! Harry Long2(Contango-Bond) harvester Live", HQUserID = HQUserID.drcharmat, IbGatewayUserToTrade = GatewayUser.CharmatSecondary,
                            MaxTradeValueInCurrency = 400000, // For Mr.C.: portfolio is 150K original. + 2019-03: +150K = 300K, Set MaxValue=400K  (assuming portfolio double in a year)
                            MinTradeValueInCurrency = 200,  //50% allocation to all assets
                            // Param = new PortfolioParamHarryLong() { Tickers = new string[] {"SVXY", "VXX", "ZIV", "TQQQ", "TMV", "UWT", "UGAZ" }, AssetsWeights = new double[] { 0.075, -0.025, 0.05, 0.125, -0.425, -0.045, -0.13 }  } }
                            Param = new PortfolioParamHarryLong() { Tickers = new string[] {"SVXY", "VXX", "ZIV", "TQQQ", "TMV", "SCO", "UGAZ" }, AssetsWeights = new double[] { 0.075, -0.025, 0.05, 0.125, -0.425, 0.0675, -0.13 }  } }  // 2020-04-02: UWT, DWT was delisted because it went to penny stock
                        //new BrokerTaskPortfolio() { Name = "! IB T. Risky 2 Live", HQUserID = HQUserID.gyantal, IbGatewayUserToTrade = GatewayUser.TuSecondary,
                        //    MaxTradeValueInCurrency = 15000, // For Tu: portfolio is 5K original. Set MaxValue=15K  (assuming portfolio double in a year)
                        //    MinTradeValueInCurrency = 200,
                        //    Param = new PortfolioParamHarryLong() {
                        //        Tickers = new string[] { "SVXY", "ZIV", "TQQQ", "TMV", "UWT", "UGAZ" }, AssetsWeights = new double[] {  0.20, 0.10, 0.25, -0.85, -0.09, -0.26 } // for T. It is safer if we don't have to login and check the shortVXX position. On the top of it, VXX $pos would be under $200, not traded by VBroker.
                        //    } }
                        }
                    }
                }
            };
            harryLongTaskSchema.Triggers.Add(new VbTrigger()
            {
                TriggeredTaskSchema = harryLongTaskSchema,
                TriggerType = TriggerType.DailyOnUsaMarketDay,
                StartTimeBase = StartTimeBase.BaseOnUsaMarketOpen,
                StartTimeOffset = TimeSpan.FromMinutes(30),
                TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            });
            harryLongTaskSchema.Triggers.Add(new VbTrigger()
            {
                TriggeredTaskSchema = harryLongTaskSchema,
                TriggerType = TriggerType.DailyOnUsaMarketDay,
                StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
                StartTimeOffset = TimeSpan.FromMinutes(-31),
                TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, true } }
            });
            harryLongTaskSchema.Triggers.Add(new VbTrigger()
            {
                TriggeredTaskSchema = harryLongTaskSchema,
                TriggerType = TriggerType.DailyOnUsaMarketDay,
                StartTimeBase = StartTimeBase.BaseOnUsaMarketClose,
                StartTimeOffset = TimeSpan.FromSeconds(-11),    // Give UberVXX priority (executing at -15sec). That is more important because that can change from full 100% long to -200% short. This Harry Long strategy just slowly modifies weights, so if one trade is missed, it is not a problem.
                TriggerSettings = new Dictionary<object, object>() { { BrokerTaskSetting.IsSimulatedTrades, false } }
            });
            g_taskSchemas.Add(harryLongTaskSchema);

        }

       

        internal void Exit() // in general exit should happen in the opposite order as Init()
        {
            m_tcpListener.StopTcpMessageListener();
            g_brokerScheduler.Exit();
            g_gatewaysWatcher.Exit();
        }
        
    }
}
