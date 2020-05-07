using Microsoft.AspNetCore.Mvc;
using SqCommon;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using SQCommon.MathNet;
using System.Numerics;

namespace SQLab.Controllers
{
    public class VolDragDataController : Controller
    {
#if !DEBUG
        [Authorize]
#endif
        public ActionResult Index(string commo)
        {
            int lbP;
            if (int.TryParse(commo, out lbP)&&lbP>1)
            {
                try
                {
                    return Content(GetStr(lbP), "text/html");
                }
                catch
                {
                    return Content(GetStr2(), "text/html");
                }
            }
            else if (commo=="JUVE")
            {
                try
                {
                    return Content(GetStr(20), "text/html");
                }
                catch
                {
                    return Content(GetStr2(), "text/html");
                }
            }
            else
            {
                return Content(GetStr2(), "text/html");
            }
        }

        public string GetStr2()
        {
            return "Error";
        }

        
        //Downloading price data from SQL Server
        public static IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>> DataSQDBGmod(string[] p_allAssetList)
        {
            Utils.Logger.Info("DataSQDBGmod() START");
            List<string> tickersNeeded = p_allAssetList.ToList();
            DateTime endTimeUtc = DateTime.UtcNow.AddDays(10);
            DateTime endTimeUtc2 = endTimeUtc.AddDays(-11);
            DateTime endTimeUtc3 = endTimeUtc.AddDays(-12);
            DateTime startTimeUtc = DateTime.ParseExact("2004/03/26", "yyyy/MM/dd", CultureInfo.InvariantCulture);

            var getAllQuotesTask = SQLab.Controllers.QuickTester.Strategies.StrategiesCommon.GetHistoricalAndRealtimesQuotesAsync(startTimeUtc, endTimeUtc, tickersNeeded);  
            Tuple<IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>>, TimeSpan, TimeSpan> getAllQuotesData = getAllQuotesTask.Result;
            
            IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>> quotes;
            
            quotes = getAllQuotesData.Item1.ToList();

            
            IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>> dataFromSQServermod = quotes;
            Utils.Logger.Info("DataSQDBGmod() END");
            return dataFromSQServermod;
        }

       

        public string GetStr(int p_lbP)
        {
            //Defining asset lists.
            string[] volAssetList = new string[] { "SVXY!Light0.5x.SQ", "VXX.SQ", "VXZ.SQ"};
            string[] volAssetListNN = new string[] { "SVXY_Light", "VXX", "VXZ"};

            //string[] volAssetList = new string[] { "SVXY!Light0.5x.SQ", "VXX.SQ", "VXZ.SQ", "UVXY!Light1.5x.SQ", "TVIX!Better1.SQ" };
            //string[] volAssetListNN = new string[] { "SVXY_Light", "VXX", "VXZ", "UVXY_Light", "TVIX_Better" };

            string[] etpAssetList = new string[] { "SPY", "UPRO.SQ", "QQQ", "TQQQ.SQ", "FAS.SQ", "TMV", "UCO", "UGLD"};
           string[] etpAssetListNN = new string[] { "SPY", "UPRO", "QQQ", "TQQQ", "FAS", "TMV", "UCO", "UGLD" };

            //string[] etpAssetList = new string[] { "SPY", "UPRO.SQ", "QQQ", "TQQQ.SQ", "FAS.SQ", "TMV", "UGAZ", "UWT", "UGLD" };
            //string[] etpAssetListNN = new string[] { "SPY", "UPRO", "QQQ", "TQQQ", "FAS", "TMV", "UGAZ", "UWT", "UGLD" };

            string[] gchAssetList = new string[] { "AAPL", "ADBE", "AMZN", "BABA", "CRM", "FB", "GOOGL", "MA", "MSFT", "NVDA", "PYPL", "QCOM", "V" };
            string[] gchAssetListNN = new string[] { "AAPL", "ADBE", "AMZN", "BABA", "CRM", "FB", "GOOGL", "MA", "MSFT", "NVDA", "PYPL", "QCOM", "V" };

            string[] gmAssetList = new string[] { "MDY", "ILF", "FEZ", "EEM", "EPP", "VNQ"};
            string[] gmAssetListNN = new string[] { "MDY", "ILF", "FEZ", "EEM", "EPP", "VNQ" };

            string[] vixAssetList = new string[] { "^VIX" };

            string[] defaultCheckedList = new string[] { "SPY", "QQQ", "VXX", "AAPL", "AMZN", "FB", "GOOGL" }; 

            var allAssetList = etpAssetList.Union(volAssetList).Union(gchAssetList).Union(gmAssetList).Union(vixAssetList).ToArray();
            var usedAssetList = etpAssetListNN.Union(volAssetListNN).Union(gchAssetListNN).Union(gmAssetListNN).ToArray();


            //string[] allAssetList = new string[]{ "SPY", "SVXY!Light0.5x.SQ", "VXX.SQ", "VXZ.SQ", "UVXY!Light1.5x.SQ", "TVIX!Better1.SQ", "TQQQ.SQ", "TCEHY", "^VIX" };
            //string[] usedAssetList = new string[] { "SPY", "SVXY_Light", "VXX", "VXZ", "UVXY_Light", "TVIX_Better", "TQQQ", "TCEHY"};

            int volLBPeriod = p_lbP;
            int[] retLB = new int[] {1, 3, 5, 10, 20, 63, 126, 252};
            string[] retLBStr = new string[] { "1 Day", "3 Days", "1 Week", "2 Weeks", "1 Month", "3 Months", "6 Months", "1 Year" };
            int retHistLB = 20;

            //Collecting and splitting price data got from SQL Server
            IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>> quotesData = DataSQDBGmod(allAssetList);
            IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>> quotesData1= new List<List<SQLab.Controllers.QuickTester.Strategies.DailyData>>(quotesData);
            quotesData1.RemoveAt(allAssetList.Length-1);
            
            List<SQLab.Controllers.QuickTester.Strategies.DailyData> quotesData2 = quotesData[allAssetList.Length-1];


            int noAssets = allAssetList.Length-1;
            int noBtDays = quotesData1[0].Count;
            DateTime[] quotesDateVec = new DateTime[noBtDays];

            for (int iRows=0; iRows<quotesDateVec.Length;iRows++)
            {
                quotesDateVec[iRows] = quotesData1[0][iRows].Date;
            }

            DateTime[] quotesFirstDates = new DateTime[noAssets];

            for (int jAssets = 0; jAssets < quotesFirstDates.Length; jAssets++)
            {
                quotesFirstDates[jAssets] = quotesData1[jAssets][0].Date;
            }

            DateTime[] quotesLastDates = new DateTime[noAssets];

            for (int jAssets = 0; jAssets < quotesLastDates.Length; jAssets++)
            {
                quotesLastDates[jAssets] = quotesData1[jAssets][quotesData1[jAssets].Count-1].Date;
            }

            double[] quotesFirstPrices = new double[noAssets];

            for (int jAssets = 0; jAssets < quotesFirstPrices.Length; jAssets++)
            {
                quotesFirstPrices[jAssets] = quotesData1[jAssets][0].AdjClosePrice;
            }

            double[] quotesLastPrices = new double[noAssets];

            for (int jAssets = 0; jAssets < quotesLastPrices.Length; jAssets++)
            {
                quotesLastPrices[jAssets] = quotesData1[jAssets][quotesData1[jAssets].Count - 1].AdjClosePrice;
            }

            IList<List<double>> quotesPrices = new List<List<double>>();

            for (int iAsset = 0; iAsset < noAssets; iAsset++)
            {
                int shiftDays = 0;
                List<double> assPriceSubList = new List<double>();
                //for (int jRows = 0; jRows < noBtDays; jRows++)
                //{
                int jRows = 0;
                    while (quotesDateVec[jRows] < quotesFirstDates[iAsset])
                    {
                        assPriceSubList.Add(quotesFirstPrices[iAsset]);
                        shiftDays += 1;
                        jRows++;
                        if (jRows >= noBtDays)
                        {
                            break;
                        }
                    }
                    while (quotesDateVec[jRows] == quotesData1[iAsset][jRows - shiftDays].Date)
                    {
                        assPriceSubList.Add(quotesData1[iAsset][jRows - shiftDays].AdjClosePrice);
                        jRows++;
                        if (jRows >= quotesData1[iAsset].Count+shiftDays)
                        {
                            break;
                        }
                    }
                    if (jRows < noBtDays)
                    {
                        while (quotesDateVec[jRows] > quotesLastDates[iAsset])
                        {
                            assPriceSubList.Add(quotesLastPrices[iAsset]);
                            jRows++;
                            if (jRows >= noBtDays)
                            {
                                break;
                            }
                        }
                    }
                //}
                quotesPrices.Add(assPriceSubList);
            }

            double[,] histRet = new double[retLB.Length,noAssets];

            for (int iAsset = 0; iAsset < noAssets; iAsset++)
            {
                for (int jRows = 0; jRows < retLB.Length; jRows++)
                {
                    histRet[jRows,iAsset]=quotesPrices[iAsset][quotesPrices[0].Count-1] / quotesPrices[iAsset][quotesPrices[0].Count - 1-retLB[jRows]] - 1;
                }
            }

            int histRetLengthSum = retLB.Sum();
            double[,] histRet2 = new double[histRetLengthSum, noAssets];

            int kShift = 0;
            for (int kLen = 0; kLen < retLB.Length; kLen++)
            {
                for (int iAsset = 0; iAsset < noAssets; iAsset++)
                {
                    for (int jRows = 0; jRows < retLB[kLen]; jRows++)
                    {
                        histRet2[kShift+jRows, iAsset] = quotesPrices[iAsset][quotesPrices[0].Count - retLB[kLen] + jRows] / quotesPrices[iAsset][quotesPrices[0].Count - 1 - retLB[kLen]] - 1;
                    }
                }
                kShift += retLB[kLen];
            }

            IList<List<double>> quotesRets = new List<List<double>>(); 

            for (int iAsset = 0; iAsset < noAssets; iAsset++)
            {
                List<double> assSubList = new List<double>();
                assSubList.Add(0);
                for (int jRows = 1; jRows < noBtDays; jRows++)
                {
                    assSubList.Add(quotesPrices[iAsset][jRows]/quotesPrices[iAsset][jRows-1]-1);
                }
                quotesRets.Add(assSubList);
            }

            IList<List<double>> assVolDrags = new List<List<double>>();
            List<double> vixQuotes = new List<double>();
            double[] vixLevel = new double[noBtDays];
            if (quotesData2.Count < noBtDays)
            {
                quotesData2.Add(quotesData2[quotesData2.Count - 1]);
            }

            for (int iRows = 0; iRows < noBtDays; iRows++)
            {
                vixQuotes.Add(quotesData2[iRows].AdjClosePrice);
            }

            for (int iAsset = 0; iAsset < noAssets; iAsset++)
            {
                List<double> assVolDragSubList = new List<double>();
                for (int jRows = 0; jRows < volLBPeriod-1; jRows++)
                {
                    assVolDragSubList.Add(0);
                    vixLevel[jRows] = Math.Round(ArrayStatistics.Mean(vixQuotes.GetRange(0,jRows).ToArray()),3);
                }
                for (int jRows = volLBPeriod-1; jRows < noBtDays; jRows++)
                {
                    assVolDragSubList.Add(ArrayStatistics.Variance(quotesRets[iAsset].GetRange(jRows - volLBPeriod + 1, volLBPeriod).ToArray())/2*21);
                    vixLevel[jRows] = Math.Round(ArrayStatistics.Mean(vixQuotes.GetRange(jRows - volLBPeriod + 1, volLBPeriod).ToArray()),3);
                }
                assVolDrags.Add(assVolDragSubList);
            }
            vixLevel[0] = quotesData2[0].AdjClosePrice;

            string[] dateYearsVec = new string[noBtDays];
            string[] dateYearsMonthsVec = new string[noBtDays];
            for (int iRows = 0; iRows < dateYearsMonthsVec.Length; iRows++)
            {
                dateYearsVec[iRows] = quotesDateVec[iRows].ToString("yyyy");
                dateYearsMonthsVec[iRows] = quotesDateVec[iRows].ToString("yyyy-MM");
            }

            //Tuple<string[], string[], IList<List<double>>> dataToCumm = Tuple.Create(dateYearsVec, dateYearsMonthsVec, assVolDrags);

            string[] dateYearsDist = dateYearsVec.Distinct().ToArray();
            string[] dateYearsMonthsDist = dateYearsMonthsVec.Distinct().ToArray();

            double[,] dateYearsAvgs = new double[dateYearsDist.Length, noAssets];
            double[] dateYearsVixAvgs = new double[dateYearsDist.Length];
            int[] dateYearsCount = new int[dateYearsDist.Length];
            int kElem = 0;
            for (int iRows = 0; iRows < dateYearsDist.Length; iRows++)
            {
                double[] subSumVec = new double[noAssets];
                double subSumVix = 0;
                while (kElem<noBtDays && dateYearsVec[kElem]==dateYearsDist[iRows])
                {
                    for (int jAssets = 0; jAssets < noAssets; jAssets++)
                    {
                        subSumVec[jAssets] = subSumVec[jAssets]+assVolDrags[jAssets][kElem];
                    }
                    subSumVix = subSumVix + vixLevel[kElem];
                    kElem++;
                    dateYearsCount[iRows] += 1;
                }
                for (int jAssets = 0; jAssets < noAssets; jAssets++)
                {
                    dateYearsAvgs[iRows, jAssets] = subSumVec[jAssets]/dateYearsCount[iRows];
                }
                dateYearsVixAvgs[iRows] = subSumVix / dateYearsCount[iRows];
            }
            int noTotalDays = dateYearsCount.Sum();

            double[,] dateYearsMonthsAvgs = new double[dateYearsMonthsDist.Length, noAssets];
            double[] dateYearsMonthsVixAvgs = new double[dateYearsMonthsDist.Length];
            int[] dateYearsMonthsCount = new int[dateYearsMonthsDist.Length];
            int kElemM = 0;
            for (int iRows = 0; iRows < dateYearsMonthsDist.Length; iRows++)
            {
                double[] subSumVec = new double[noAssets];
                double subSumVix = 0;
                while (kElemM < noBtDays && dateYearsMonthsVec[kElemM] == dateYearsMonthsDist[iRows])
                {
                    for (int jAssets = 0; jAssets < noAssets; jAssets++)
                    {
                        subSumVec[jAssets] = subSumVec[jAssets] + assVolDrags[jAssets][kElemM];
                    }
                    subSumVix = subSumVix + vixLevel[kElemM];
                    kElemM++;
                    dateYearsMonthsCount[iRows] += 1;
                }
                for (int jAssets = 0; jAssets < noAssets; jAssets++)
                {
                    dateYearsMonthsAvgs[iRows, jAssets] = subSumVec[jAssets] / dateYearsMonthsCount[iRows];
                }
                dateYearsMonthsVixAvgs[iRows] = subSumVix / dateYearsMonthsCount[iRows];
            }

            double vixAvgTotal = ArrayStatistics.Mean(vixLevel);
            double[] volDragsAvgsTotal = new double[noAssets];
            for (int jAssets = 0; jAssets < noAssets; jAssets++)
            {
                int numEl = 0;
                double subSum = 0;
                for (int iRows = 0; iRows < noBtDays; iRows++)
                    if (assVolDrags[jAssets][iRows] > 0)
                    {
                        subSum = subSum + assVolDrags[jAssets][iRows];
                        numEl += 1;
                    }

                volDragsAvgsTotal[jAssets] = subSum/numEl;
            }

            //Request time (UTC)
            DateTime liveDateTime = DateTime.UtcNow;
            string liveDate = System.String.Empty;
            liveDate = liveDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            DateTime timeNowET = Utils.ConvertTimeFromUtcToEt(liveDateTime);
            string liveDateString = "Request time (UTC): " + liveDate;

            //Last data time (UTC)
            string lastDataTime = (quotesData[0][quotesData[0].Count - 1].Date.Date == liveDateTime.Date & timeNowET.TimeOfDay<=new DateTime(2000,1,1,16,15,0).TimeOfDay) ? "Live data at " + liveDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "Close price on "+ quotesData[0][quotesData[0].Count - 1].Date.ToString("yyyy-MM-dd");
            string lastDataTimeString = "Last data time (UTC): "+lastDataTime;



            ////Creating input string for JavaScript.
            StringBuilder sb = new StringBuilder("{" + Environment.NewLine);
            sb.Append(@"""requestTime"": """ + liveDateString);
            sb.Append(@"""," + Environment.NewLine + @"""lastDataTime"": """ + lastDataTimeString);

            sb.Append(@"""," + Environment.NewLine + @"""volLBPeri"": """ + volLBPeriod);
            sb.Append(@"""," + Environment.NewLine + @"""retHistLBPeri"": """ + retHistLB);
            
            sb.Append(@"""," + Environment.NewLine + @"""retLBPeris"": """);
            for (int i = 0; i < retLB.Length - 1; i++)
                sb.Append(retLBStr[i] + ", ");
            sb.Append(retLBStr[retLBStr.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""retLBPerisNo"": """);
            for (int i = 0; i < retLB.Length - 1; i++)
                sb.Append(retLB[i] + ", ");
            sb.Append(retLB[retLB.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""assetNames"": """);
            for (int i = 0; i < usedAssetList.Length - 1; i++)
                sb.Append(usedAssetList[i] + ", ");
            sb.Append(usedAssetList[usedAssetList.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""defCheckedList"": """);
            for (int i = 0; i < defaultCheckedList.Length - 1; i++)
                sb.Append(defaultCheckedList[i] + ", ");
            sb.Append(defaultCheckedList[defaultCheckedList.Length - 1]);

            

            sb.Append(@"""," + Environment.NewLine + @"""volAssetNames"": """);
            for (int i = 0; i < volAssetListNN.Length - 1; i++)
                sb.Append(volAssetListNN[i] + ", ");
            sb.Append(volAssetListNN[volAssetListNN.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""etpAssetNames"": """);
            for (int i = 0; i < etpAssetListNN.Length - 1; i++)
                sb.Append(etpAssetListNN[i] + ", ");
            sb.Append(etpAssetListNN[etpAssetListNN.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""gchAssetNames"": """);
            for (int i = 0; i < gchAssetListNN.Length - 1; i++)
                sb.Append(gchAssetListNN[i] + ", ");
            sb.Append(gchAssetListNN[gchAssetListNN.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""gmAssetNames"": """);
            for (int i = 0; i < gmAssetListNN.Length - 1; i++)
                sb.Append(gmAssetListNN[i] + ", ");
            sb.Append(gmAssetListNN[gmAssetListNN.Length - 1]);
            
            sb.Append(@"""," + Environment.NewLine + @"""quotesDateVector"": """);
            for (int i = 0; i < quotesDateVec.Length - 1; i++)
                sb.Append(quotesDateVec[i].ToString("yyyy-MM-dd") + ", ");
            sb.Append(quotesDateVec[quotesDateVec.Length-1].ToString("yyyy-MM-dd"));

            sb.Append(@"""," + Environment.NewLine + @"""dailyVolDrags"": """);
            for (int i = 0; i < assVolDrags[0].Count; i++)
            {
                sb.Append("");
                for (int j = 0; j < assVolDrags.Count - 1; j++)
                {
                    sb.Append(Math.Round(assVolDrags[j][i]*100,2).ToString() + "%, ");
                }
                sb.Append(Math.Round(assVolDrags[assVolDrags.Count - 1][i]*100,2).ToString() + "%");
                if (i < assVolDrags[0].Count - 1)
                {
                    sb.Append("ß ");
                }
            }

            sb.Append(@"""," + Environment.NewLine + @"""dailyVIXMas"": """);
            for (int i = 0; i < vixLevel.Length - 1; i++)
                sb.Append(Math.Round(vixLevel[i],2).ToString() + ", ");
            sb.Append(Math.Round(vixLevel[vixLevel.Length - 1],2).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""yearList"": """);
            for (int i = 0; i < dateYearsDist.Length - 1; i++)
                sb.Append(dateYearsDist[i] + ", ");
            sb.Append(dateYearsDist[dateYearsDist.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""yearMonthList"": """);
            for (int i = 0; i < dateYearsMonthsDist.Length - 1; i++)
                sb.Append(dateYearsMonthsDist[i] + ", ");
            sb.Append(dateYearsMonthsDist[dateYearsMonthsDist.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""yearlyAvgs"": """);
            for (int i = 0; i < dateYearsAvgs.GetLength(0); i++)
            {
                sb.Append("");
                for (int j = 0; j < dateYearsAvgs.GetLength(1) - 1; j++)
                {
                    sb.Append(Math.Round(dateYearsAvgs[i,j] * 100, 2).ToString() + "%, ");
                }
                sb.Append(Math.Round(dateYearsAvgs[i,dateYearsAvgs.GetLength(1)-1] * 100, 2).ToString() + "%");
                if (i < dateYearsAvgs.GetLength(0)-1)
                {
                    sb.Append("ß ");
                }
            }

            sb.Append(@"""," + Environment.NewLine + @"""monthlyAvgs"": """);
            for (int i = 0; i < dateYearsMonthsAvgs.GetLength(0); i++)
            {
                sb.Append("");
                for (int j = 0; j < dateYearsMonthsAvgs.GetLength(1) - 1; j++)
                {
                    sb.Append(Math.Round(dateYearsMonthsAvgs[i, j] * 100, 2).ToString() + "%, ");
                }
                sb.Append(Math.Round(dateYearsMonthsAvgs[i, dateYearsMonthsAvgs.GetLength(1) - 1] * 100, 2).ToString() + "%");
                if (i < dateYearsMonthsAvgs.GetLength(0) - 1)
                {
                    sb.Append("ß ");
                }
            }

            sb.Append(@"""," + Environment.NewLine + @"""yearlyVIXAvgs"": """);
            for (int i = 0; i < dateYearsVixAvgs.Length - 1; i++)
                sb.Append(Math.Round(dateYearsVixAvgs[i], 2).ToString() + ", ");
            sb.Append(Math.Round(dateYearsVixAvgs[dateYearsVixAvgs.Length - 1], 2).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""monthlyVIXAvgs"": """);
            for (int i = 0; i < dateYearsMonthsVixAvgs.Length - 1; i++)
                sb.Append(Math.Round(dateYearsMonthsVixAvgs[i], 2).ToString() + ", ");
            sb.Append(Math.Round(dateYearsMonthsVixAvgs[dateYearsMonthsVixAvgs.Length - 1], 2).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""yearlyCounts"": """);
            for (int i = 0; i < dateYearsCount.Length - 1; i++)
                sb.Append(dateYearsCount[i].ToString() + ", ");
            sb.Append(dateYearsCount[dateYearsCount.Length - 1].ToString());
                        
            sb.Append(@"""," + Environment.NewLine + @"""noTotalDays"": """ + noTotalDays);

            sb.Append(@"""," + Environment.NewLine + @"""monthlyCounts"": """);
            for (int i = 0; i < dateYearsMonthsCount.Length - 1; i++)
                sb.Append(dateYearsMonthsCount[i].ToString() + ", ");
            sb.Append(dateYearsMonthsCount[dateYearsMonthsCount.Length - 1].ToString());

            sb.Append(@"""," + Environment.NewLine + @"""vixAvgTotal"": """ + Math.Round(vixAvgTotal,2).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""volDragsAvgsTotalVec"": """);
            for (int i = 0; i < volDragsAvgsTotal.Length - 1; i++)
                sb.Append(Math.Round(volDragsAvgsTotal[i]*100,2).ToString() + "%, ");
            sb.Append(Math.Round(volDragsAvgsTotal[volDragsAvgsTotal.Length - 1]*100,2).ToString() + "%");

            sb.Append(@"""," + Environment.NewLine + @"""histRetMtx"": """);
            for (int i = 0; i < histRet.GetLength(0); i++)
            {
                sb.Append("");
                for (int j = 0; j < histRet.GetLength(1) - 1; j++)
                {
                    sb.Append(Math.Round(histRet[i, j] * 100, 2).ToString() + "%, ");
                }
                sb.Append(Math.Round(histRet[i, histRet.GetLength(1) - 1] * 100, 2).ToString() + "%");
                if (i < histRet.GetLength(0) - 1)
                {
                    sb.Append("ß ");
                }
            }

            sb.Append(@"""," + Environment.NewLine + @"""histRet2Chart"": """);
            for (int i = 0; i < histRet2.GetLength(0); i++)
            {
                sb.Append("");
                for (int j = 0; j < histRet2.GetLength(1) - 1; j++)
                {
                    sb.Append(Math.Round(histRet2[i, j] * 100, 2).ToString() + "%, ");
                }
                sb.Append(Math.Round(histRet2[i, histRet2.GetLength(1) - 1] * 100, 2).ToString() + "%");
                if (i < histRet2.GetLength(0) - 1)
                {
                    sb.Append("ß ");
                }
            }

            sb.AppendLine(@"""" + Environment.NewLine + @"}");

            var asdfa = sb.ToString(); //testing created string to JS

            return sb.ToString();
           
        }

        
    }
}
