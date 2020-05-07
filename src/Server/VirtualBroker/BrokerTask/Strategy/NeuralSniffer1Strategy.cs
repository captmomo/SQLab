using DbCommon;
using IBApi;
using SqCommon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VirtualBroker.Strategy.NeuralSniffer;
using Utils = SqCommon.Utils;

namespace VirtualBroker
{

    // Portfolio specific parameters are here. User1's portfolio1 may use double leverage than User2's portfolio2. The Common Strategy params should go to StrategyConfig.cs
    public class PortfolioParamNeuralSniffer1 : IPortfolioParam
    {
        public double PlayingInstrumentUpsideLeverage { get; set; } // negative number means: play the inverse; instead of long, short the inverse
        public double PlayingInstrumentDownsideLeverage { get; set; }
    }

    public class NeuralSniffer1Strategy : IBrokerStrategy
    {
        StringBuilder m_detailedReportSb;

        Task<Dictionary<string, Tuple<IAssetID, string>>> m_loadAssetIdTask = null;
        Dictionary<string, Tuple<IAssetID, string>> m_tickerToAssetId = null;   // ticker => AssetID : FullTicker mapping

        public bool IsSameStrategyForAllUsers { get; set; } = true;

        NNConfig nnConfig = new NNConfig();
        //List<QuoteData> m_rut;

        //TODO: implement isNextDayInBullishHolidayRange, but think that UberVXX will use it too. So the solution has to be general, not a simple hack
        // see book Seasonal Stock Market Trends 2009_agy.pdf
        // we consider only Xmas and New Years's eve as bullish holidays  (later we added Good Friday (eastern) and Thanksgiving)
        // because there is some overlap, in 2011 december, it gives only 3+4+3=10 trading days (not 12 as expected)
        //Based on http://www.cobrasmarketview.com/holiday-seasonality/
        //add to goodHolidays: Good Friday (eastern), and Thanksgiving day (Nov. 22, good Novemer seasonality)
        // see future dates from here: http://www.timeanddate.com/holidays/us/
        static readonly DateTime[] g_bullishHolidaysInET = new DateTime[] { new DateTime(2011, 12, 26),
            new DateTime(2012, 01, 02), new DateTime(2012, 04, 06), new DateTime(2012, 11, 22), new DateTime(2012, 12, 25),
            new DateTime(2013, 01, 01), new DateTime(2013, 03, 29), new DateTime(2013, 11, 28), new DateTime(2013, 12, 25),
            new DateTime(2014, 01, 01), new DateTime(2014, 04, 18), new DateTime(2014, 11, 27), new DateTime(2014, 12, 25),
            new DateTime(2015, 01, 01), new DateTime(2015, 04, 03), new DateTime(2015, 11, 26), new DateTime(2015, 12, 25),
            new DateTime(2016, 01, 01), new DateTime(2016, 03, 25), new DateTime(2016, 11, 24), new DateTime(2016, 12, 26),
            new DateTime(2017, 01, 01), new DateTime(2017, 04, 14), new DateTime(2017, 11, 23), new DateTime(2017, 12, 25),
            new DateTime(2018, 01, 01), new DateTime(2018, 03, 30), new DateTime(2018, 11, 22), new DateTime(2018, 12, 25),
            new DateTime(2019, 01, 01), new DateTime(2019, 04, 19), new DateTime(2019, 11, 28), new DateTime(2019, 12, 25),

        };

        public static IBrokerStrategy StrategyFactoryCreate()
        {
            return new NeuralSniffer1Strategy();
        }

        public void Init(StringBuilder p_detailedReportSb)
        {
            m_detailedReportSb = p_detailedReportSb;
            m_loadAssetIdTask = Task.Run(() => DbCommon.SqlTools.LoadAssetIdsForTickers(new List<string>() { "UWM", "TWM" }));   // task will start to run on another thread (in the threadpool)
        }

        public string StockIdToTicker(int p_stockID)
        {
            if (m_tickerToAssetId == null)
                m_tickerToAssetId = m_loadAssetIdTask.Result;   // wait until the parrallel task arrives
            return m_tickerToAssetId.First(r => r.Value.Item1.ID == p_stockID).Key;
        }

        public IAssetID TickerToAssetID(string p_ticker)
        {
            if (m_tickerToAssetId == null)
                m_tickerToAssetId = m_loadAssetIdTask.Result;   // wait until the parrallel task arrives
            return m_tickerToAssetId[p_ticker].Item1;
        }

        public List<PortfolioPositionSpec> GeneratePositionSpecs(IPortfolioParam p_portfolioParam)
        {
            Utils.Logger.Info("NeuralSniffer1Strategy.GeneratePositionSpecs() Begin.");
            int lookbackWindowSize = nnConfig.lookbackWindowSize + 3;       // in general we want 202 historical + 1 today price => that will generate 202 %change bars (but because last 2 days data is used, that will be 200 samples)

            bool isNextDayInBullishHolidayRange = false;
            List<QuoteData> m_rut;      // contains today real-time price too

            DateTime dateNowInET = Utils.ConvertTimeFromUtcToEt(DateTime.UtcNow);
            DateTime nextTradingDayUtc = Utils.GetNextUsaMarketOpenDayUtc(DateTime.UtcNow, false);
            DateTime nextTradingDayET = Utils.ConvertTimeFromUtcToEt(nextTradingDayUtc);
            nextTradingDayET = nextTradingDayET.Date;
            foreach (DateTime bullishHolidayDateET in g_bullishHolidaysInET)
            {
                // we want to be long in the [-3 days, +3 days], so generate 3 days before, 3 days after, and check if nextTradingDayET is that
                if (Math.Abs((bullishHolidayDateET - nextTradingDayET).TotalDays) > 20)     // if holiday is further than 20 days, there is no point to check
                    continue;

                DateTime bullishHolidayDateTimeET = bullishHolidayDateET.AddHours(12); // consider 12 hours later = noon
                DateTime bullishHolidayDateTimeUtc = Utils.ConvertTimeFromEtToUtc(bullishHolidayDateTimeET);

                // 1. Check if nextTradingDayET is in the previous 3 days;
                DateTime candidateDateTimeUtc = bullishHolidayDateTimeUtc;
                for (int i = 0; i < 3; i++)
                {
                    candidateDateTimeUtc = Utils.GetPreviousUsaMarketOpenDayUtc(candidateDateTimeUtc, false);
                    DateTime candidateDateTimeET = Utils.ConvertTimeFromUtcToEt(candidateDateTimeUtc);
                    DateTime candidateDateET = candidateDateTimeET.Date;
                    if (candidateDateET == nextTradingDayET)
                    {
                        isNextDayInBullishHolidayRange = true;
                        break;
                    }
                }
                if (isNextDayInBullishHolidayRange)
                    break;

                // 1. Check if nextTradingDayET is in the next 3 days;
                candidateDateTimeUtc = bullishHolidayDateTimeUtc;
                for (int i = 0; i < 3; i++)
                {
                    candidateDateTimeUtc = Utils.GetNextUsaMarketOpenDayUtc(candidateDateTimeUtc, false);
                    DateTime candidateDateTimeET = Utils.ConvertTimeFromUtcToEt(candidateDateTimeUtc);
                    DateTime candidateDateET = candidateDateTimeET.Date;
                    if (candidateDateET == nextTradingDayET)
                    {
                        isNextDayInBullishHolidayRange = true;
                        break;
                    }
                }
                if (isNextDayInBullishHolidayRange)
                    break;
            }


            double forecast = Double.NaN;
            if (isNextDayInBullishHolidayRange)
            {
                forecast = 1;
                Utils.ConsoleWriteLine(ConsoleColor.Green, false, $"Bullish Holiday Range[-3,+3 days] around NewYear, GoodFriday, Thanksgiving, Xmas. Final RUT Forecast: { forecast * 100}%");
                Utils.Logger.Info($"Bullish Holiday Range[-3,+3 days] around NewYear, GoodFriday, Thanksgiving, Xmas. Final RUT Forecast: { forecast * 100}%");
                m_detailedReportSb.AppendLine($"<font color=\"#10ff10\">Bullish Holiday Range[-3, +3 days] around NewYear, GoodFriday, Thanksgiving, Xmas.Final RUT Forecast: { forecast * 100}%</font>");
            }
            else
            {
                // 1. Get historical RUT data from SQL db. IB only gives 1 year of historical data, so we better get it from DB.
                Utils.Logger.Info("NeuralSniffer1Strategy.GeneratePositionSpecs() SqlTools.LoadHistoricalQuotesAsync().");
                List<QuoteData> rutQuotesFromSqlDB = SqlTools.LoadHistoricalQuotesAsync(new[] {
                    new QuoteRequest { Ticker = "^RUT", nQuotes =  lookbackWindowSize - 1 }}, DbCommon.AssetType.BenchmarkIndex).Result.
                    Select(row => new QuoteData { Date = (DateTime)row[1], AdjClosePrice = (double)Convert.ToDecimal(row[2]) }).OrderBy(row => row.Date).ToList(); // stocks come as double objects: (double)row[2], indexes as floats  (double)(float)row[2]

                m_rut = rutQuotesFromSqlDB.Select(item => new QuoteData() { Date = item.Date, AdjClosePrice = item.AdjClosePrice }).ToList(); // Clone the SQL version, not YF

                //You can use IB historical data:     >for stocks OR > popular indices, like SPX, but not the RUT. > So, for RUT, implement getting historical from our SQL DB.
                //Contract contract = new Contract() { Symbol = "RUT", SecType = "IND", Currency = "USD", Exchange = "RUSSELL" };
                Contract contract = VBrokerUtils.ParseSqTickerToContract("^RUT");
                //Contract contract = new Contract() { Symbol = "SPX", SecType = "IND", Currency = "USD", Exchange = "CBOE" };
                //Contract contract = new Contract() { Symbol = "RUT", SecType = "IND", Currency = "USD", Exchange = "RUSSELL", LocalSymbol = "RUT" };
                //Contract contract = new Contract() { Symbol = "ES", SecType = "IND", Currency = "USD", Exchange = "GLOBEX" };
                //Contract contract = new Contract() { Symbol = "RUT", SecType = "IND", Currency = "USD", Exchange = "SMART" };
                //Contract contract = new Contract() { Symbol = "VXX", SecType = "STK", Currency = "USD", Exchange = "SMART" };
                //if (!Controller.g_gatewaysWatcher.ReqHistoricalData(DateTime.UtcNow, lookbackWindowSize, "TRADES", contract, out m_rut))   // real trades, not the MidPoint = AskBidSpread
                //    return null;

                // 2. Get realtime RUT data (if IBGateway doesn't give it), but IBGateway gives it.
                var rtPrices = new Dictionary<int, PriceAndTime>() { { TickType.LAST, new PriceAndTime() }, { TickType.CLOSE, new PriceAndTime() } };    // we are interested in the following Prices
                StrongAssert.True(Controller.g_gatewaysWatcher.GetAlreadyStreamedPrice(contract, ref rtPrices), Severity.ThrowException, $"There is no point continuing if rtPrice cannot be obtained for ticker '{contract.Symbol}'.");
                double rus2000LastCloseIB = rtPrices[TickType.CLOSE].Price;

                // Only for RUT index: This safety check has to be split for normal weekdays (1 day difference) for normal weekends (3 days difference) vs. 3-days-weekend (4+ days difference) because after 3-days weekends IB gives wrong PreviousClose: it gives a calculated one for Monday when there was no trading. However, the last one is the Friday one in our database. This is probably because IB can use 'calculated' indices instead of the one officially given by the exchange.
                var daysSinceLastSQLPrice = (dateNowInET.Date - m_rut[m_rut.Count - 1].Date).TotalDays;
                if (daysSinceLastSQLPrice <= 3)
                {
                    // on 2016-05-02, RUT close price: YF: 1130.85, GF and IB: 1130.84, therefore 1 penny difference should be accepted.
                    // on 2016-05-20, RUT close price: YF: 1,094.76, GF and IB: 1,094.78, therefore 2 penny difference should be accepted.
                    StrongAssert.True(Utils.IsNear(rus2000LastCloseIB, m_rut[m_rut.Count - 1].AdjClosePrice, 0.025), Severity.NoException, $"Warning only! RUT: IB Last Close price ({rus2000LastCloseIB}) should be the same as last Close in SQL DB ({m_rut[m_rut.Count - 1].AdjClosePrice}). Maybe SQL DB has no yesterday data. Execution and trading will continue as usual by using last known price from SQL DB  ({m_rut[m_rut.Count - 1].AdjClosePrice}) as yesterday data. However, it is better to check the crawlers that fills SQL DB.");
                }
                else
                {
                    // after 3 days-weekends (Sa-Su-Mo) rus2000LastCloseIB = Monday's calculated RUT close, which is not in our database, so we cannot do safety check, but it is OK. After long weekends we simply trust the SQL database data.
                    StrongAssert.True(daysSinceLastSQLPrice <= 6, Severity.NoException, $"Warning! There is no RUT historical data in the SQL DB for the last 7 days. It is likely an error. Execution and trading will continue as usual by using last known price from SQL DB  ({m_rut[m_rut.Count - 1].AdjClosePrice}) as yesterday data. However, it is better to check the crawlers that fills SQL DB.");
                }


                double rus2000Last = rtPrices[TickType.LAST].Price;    // as an Index, there is no Ask,Bid, therefore, there is no MidPrice, only LastPrice
                // append an extra CSVData representing today close value (estimating)
                m_rut.Add(new QuoteData() { Date = dateNowInET, AdjClosePrice = rus2000Last });


                // Check danger after stock split correctness: adjusted price from IB should match to the adjusted price of our SQL DB. Although it can happen that both data source is faulty.
                if (Utils.IsInRegularUsaTradingHoursNow(TimeSpan.FromDays(3))) // in development, we often program code after IB market closed. Ignore this warning after market, but check it during market.
                    StrongAssert.True(Math.Abs(rutQuotesFromSqlDB[rutQuotesFromSqlDB.Count - 1].AdjClosePrice - m_rut[m_rut.Count - 2].AdjClosePrice) < 0.02, Severity.NoException,
                        $"Yesterday close price for {contract.Symbol} doesn't match between IB ({m_rut[m_rut.Count - 2].AdjClosePrice}) and SQL DB ({rutQuotesFromSqlDB[rutQuotesFromSqlDB.Count - 1].AdjClosePrice}). Something is not right. We compare SQL data to SQL data here, so this warning shouldn't have been tiggered. For RUT, we use SQL (YF) historical data (can be wrong for the previous close temporarily). For UberVXX VXX, we use IB historical data (more trustworthy).");

                // log the last 3 values (for later debugging)
                Utils.Logger.Trace($"{m_rut[m_rut.Count - 3].Date.ToString("yyyy-MM-dd")}: {m_rut[m_rut.Count - 3].AdjClosePrice}");
                Utils.Logger.Trace($"{m_rut[m_rut.Count - 2].Date.ToString("yyyy-MM-dd")}: {m_rut[m_rut.Count - 2].AdjClosePrice}");
                Utils.Logger.Trace($"{m_rut[m_rut.Count - 1].Date.ToString("yyyy-MM-dd")}: {m_rut[m_rut.Count - 1].AdjClosePrice} (!Last trade, not last midPoint)");


                // 3. Process it
                DateTime[] dates = new DateTime[m_rut.Count - 1];
                double[] barChanges = new double[m_rut.Count - 1];
                double[] dateWeekDays = new double[m_rut.Count - 1];
                for (int i = 0; i < barChanges.Length; i++)
                {
                    dates[i] = m_rut[i + 1].Date;
                    barChanges[i] = m_rut[i + 1].AdjClosePrice / m_rut[i].AdjClosePrice - 1;
                    dateWeekDays[i] = (byte)(m_rut[i + 1].Date.DayOfWeek) - 1 - 2;     // Monday is -2, Friday is 2
                }
                double dailyPercentChange = barChanges[barChanges.Length - 1];

                // double target = p_barChanges[iRebalance + 1]; // so target is the p_iRebalance+1 day %change; so the last index that can be used in training is p_barChanges[p_iRebalance] as output
                // so, set p_iRebalance to the last usable day index (the last index)
                double avgTrainError = Double.NaN;

                int generalNensembleGroupMembers = 5;
                nnConfig.ensembleGroups = new EnsembleGroupSetup[]
                {    // keep the ensembleMembers number odd; because if it is even, 5 Up prediction and 5 down prediction can cancel each other
                        new EnsembleGroupSetup() { Nneurons = 1, NNInputDesc = NNInputDesc.BarChange, BarChangeLookbackDaysInds= new int[] { 0, 1 }, NensembleGroupMembers = generalNensembleGroupMembers }
                };

                nnConfig.nEnsembleRepeat = 21;
                forecast = new NeuralSniffer().GetEnsembleRepeatForecast(nnConfig.nEnsembleRepeat, nnConfig.ensembleGroups, nnConfig.ensembleAggregation, nnConfig.maxEpoch, dateWeekDays.Length - 1, nnConfig.lookbackWindowSize, nnConfig.outputOutlierThreshold, nnConfig.inputOutlierClipInSD, nnConfig.inputNormalizationBoost, nnConfig.outputNormalizationBoost, nnConfig.notNNStrategy, dateWeekDays, barChanges, true, out avgTrainError);

                Utils.ConsoleWriteLine(ConsoleColor.Green, false, $"RUT % Chg:{ dailyPercentChange * 100.0:F2}%, Final RUT Forecast: { forecast * 100}%");
                Utils.Logger.Info($"RUT %Chg:{dailyPercentChange * 100.0:F2}%, Final RUT Forecast:{forecast * 100}%");
                m_detailedReportSb.AppendLine($"<font color=\"#10ff10\">RUT %Chg:{dailyPercentChange * 100.0:F2}%, Final RUT Forecast:{forecast * 100}%</font>");
            }

            List<PortfolioPositionSpec> specs = new List<PortfolioPositionSpec>();
            if (forecast > 0) // bullish
            {
                // IBrokerStrategy should not really know about the Trading Execution, or the proper trading instrument of the user or leverage of the user. 
                // The BrokerTask should overwrite the trading instrument if it wants that suits to the different portfolios
                // but we want to Calculate this complex Strategy only once, just giving a guideline for the BrokerTask
                specs.Add(new PortfolioPositionSpec() { Ticker = "TWM", PositionType = PositionType.Short, Size = WeightedSize.Create(1.0) });
            }
            else if (forecast < 0)  // bearish
            {
                specs.Add(new PortfolioPositionSpec() { Ticker = "UWM", PositionType = PositionType.Short, Size = WeightedSize.Create(1.0) });
            }
            else
                Utils.Logger.Warn("Stay in cash");

            return specs;
        }

        public double GetPortfolioLeverage(List<PortfolioPositionSpec> p_suggestedPortfItems, IPortfolioParam p_param)
        {
            return 1.0; // TWM and UWM are already leveraged, so don't increase leverage more
        }
    }
}
