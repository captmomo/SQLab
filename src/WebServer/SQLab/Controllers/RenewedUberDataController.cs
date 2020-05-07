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
    public class RenewedUberDataController : Controller
    {
#if !DEBUG
        [Authorize]
#endif
        public ActionResult Index()
        {
            try
            {
                return Content(GetStr(), "text/html");

            }
            catch
            {
                return Content(GetStr2(), "text/html");
            }
        }

        public string GetStr2()
        {
            return "Error";
        }

        //Get event, current position data from Google Spreadsheet - George
        [HttpGet]
        public ActionResult UberRenewedGoogleApiGsheet1(string p_usedGSheetRef)
        {
            Utils.Logger.Info("UberRenewedGoogleApiGsheet1() BEGIN");

            string valuesFromGSheetStr = "Error. Make sure GoogleApiKeyKey, GoogleApiKeyKey is in SQLab.WebServer.SQLab.NoGitHub.json !";
            if (!String.IsNullOrEmpty(Utils.Configuration["GoogleApiKeyName"]) && !String.IsNullOrEmpty(Utils.Configuration["GoogleApiKeyKey"]))
            {
                if (!Utils.DownloadStringWithRetry(out valuesFromGSheetStr, p_usedGSheetRef + Utils.Configuration["GoogleApiKeyKey"], 3, TimeSpan.FromSeconds(2), true))
                    valuesFromGSheetStr = "Error in DownloadStringWithRetry().";
            }

            Utils.Logger.Info("UberRenewedGoogleApiGsheet1() END");
            return Content($"<HTML><body>UberRenewedGoogleApiGsheet1() finished OK. <br> Received data: '{valuesFromGSheetStr}'</body></HTML>", "text/html");
        }

        //Selecting, splitting data got from GSheet
        public static Tuple<double[], DateTime[], string[,], int[], int[], int[], string[]> GSheetConverter(string p_gSheetString, string[] p_allAssetList)
        {
            string[] gSheetTableRows = p_gSheetString.Split(new string[] { "[" }, StringSplitOptions.RemoveEmptyEntries);
            string currPosRaw = gSheetTableRows[3];
            currPosRaw = currPosRaw.Replace("\n", "").Replace("]", "").Replace("\",", "BRB").Replace("\"", "").Replace(" ", "").Replace(",", "");
            string[] currPos = currPosRaw.Split(new string[] { "BRB" }, StringSplitOptions.RemoveEmptyEntries);
            string[] currPosAP = new string[p_allAssetList.Length];
            Array.Copy(currPos, 2, currPosAP, 0, p_allAssetList.Length);
            int currPosDate = Int32.Parse(currPos[0]);
            int currPosCash = Int32.Parse(currPos[currPos.Length - 3]);
            int[] currPosDateCash = new int[] { currPosDate, currPosCash };
            int[] currPosAssets = Array.ConvertAll(currPosAP, int.Parse);


            p_gSheetString = p_gSheetString.Replace("\n", "").Replace("]", "").Replace("\"", "").Replace(" ", "").Replace(",,", ",0,").Replace(",,", ",0,");
            gSheetTableRows = p_gSheetString.Split(new string[] { "[" }, StringSplitOptions.RemoveEmptyEntries);

            string[,] gSheetCodes = new string[gSheetTableRows.Length - 4, currPos.Length];
            string[] gSheetCodesH = new string[currPos.Length];
            for (int iRows = 0; iRows < gSheetCodes.GetLength(0); iRows++)
            {
                gSheetCodesH = gSheetTableRows[iRows + 4].Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
                for (int jCols = 0; jCols < gSheetCodes.GetLength(1); jCols++)
                {
                    gSheetCodes[iRows, jCols] = gSheetCodesH[jCols];
                }
            }

            gSheetCodes[gSheetCodes.GetLength(0) - 1, gSheetCodes.GetLength(1) - 1] = gSheetCodesH[gSheetCodesH.Length - 1].Substring(0, gSheetCodesH[gSheetCodesH.Length - 1].IndexOf('}'));

            double[] gSheetDateVec = new double[gSheetCodes.GetLength(0)];
            for (int iRows = 0; iRows < gSheetDateVec.Length; iRows++)
            {
                gSheetDateVec[iRows] = Double.Parse(gSheetCodes[iRows, 0]);
            }

            DateTime[] gSheetRealDateVec = new DateTime[gSheetCodes.GetLength(0)];
            for (int iRows = 0; iRows < gSheetRealDateVec.Length; iRows++)
            {
                gSheetRealDateVec[iRows] = DateTime.Parse(gSheetCodes[iRows, 1]);
            }

            string[,] gSheetCodesAssets = new string[gSheetCodes.GetLength(0), p_allAssetList.Length + 1];
            for (int iRows = 0; iRows < gSheetCodesAssets.GetLength(0); iRows++)
            {
                for (int jCols = 0; jCols < gSheetCodesAssets.GetLength(1); jCols++)
                {
                    gSheetCodesAssets[iRows, jCols] = gSheetCodes[iRows, jCols + 3];
                }
            }

            string[] gSheetEventFinalSignal = new string[gSheetCodes.GetLength(0)];
            for (int iRows = 0; iRows < gSheetEventFinalSignal.Length; iRows++)
            {
                gSheetEventFinalSignal[iRows] = gSheetCodes[iRows, 2];
            }

            int[] gSheetEventCodes = new int[gSheetCodes.GetLength(0)];
            for (int iRows = 0; iRows < gSheetEventCodes.Length; iRows++)
            {
                gSheetEventCodes[iRows] = Int32.Parse(gSheetCodes[iRows, gSheetCodes.GetLength(1) - 1]);
            }


            Tuple<double[], DateTime[], string[,], int[], int[], int[], string[]> gSheetResFinal = Tuple.Create(gSheetDateVec, gSheetRealDateVec, gSheetCodesAssets, gSheetEventCodes, currPosDateCash, currPosAssets, gSheetEventFinalSignal);

            return gSheetResFinal;
        }

        //Downloading price data from SQL Server
        public static Tuple<IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>>, List<SQLab.Controllers.QuickTester.Strategies.DailyData>> DataSQDBG(string[] p_allAssetList)
        {
            List<string> tickersNeeded = p_allAssetList.ToList();
            DateTime endTimeUtc = DateTime.UtcNow.AddDays(10);
            DateTime endTimeUtc2 = endTimeUtc.AddDays(-11);
            DateTime endTimeUtc3 = endTimeUtc.AddDays(-12);
            DateTime startTimeUtc = endTimeUtc.AddDays(-50);

            var getAllQuotesTask = SQLab.Controllers.QuickTester.Strategies.StrategiesCommon.GetHistoricalAndRealtimesQuotesAsync(startTimeUtc, endTimeUtc, tickersNeeded);
            Tuple<IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>>, TimeSpan, TimeSpan> getAllQuotesData = getAllQuotesTask.Result;

            int[] histLength = new int[p_allAssetList.Length];
            for (int i = 0; i < histLength.Length; i++)
            {
                histLength[i] = getAllQuotesData.Item1[i].Count;
            }

            int allDataLength = (histLength.Min() == histLength.Max()) ? histLength.Min() : 0;

            if (allDataLength == 0)
            {
                getAllQuotesTask = SQLab.Controllers.QuickTester.Strategies.StrategiesCommon.GetHistoricalAndRealtimesQuotesAsync(startTimeUtc, endTimeUtc2, tickersNeeded);
                getAllQuotesData = getAllQuotesTask.Result;

                int[] histLength2 = new int[p_allAssetList.Length];
                for (int i = 0; i < histLength2.Length; i++)
                {
                    histLength2[i] = getAllQuotesData.Item1[i].Count;
                }
                allDataLength = (histLength2.Min() == histLength2.Max()) ? histLength2.Min() : 0;
            }

            if (allDataLength == 0)
            {
                getAllQuotesTask = SQLab.Controllers.QuickTester.Strategies.StrategiesCommon.GetHistoricalAndRealtimesQuotesAsync(startTimeUtc, endTimeUtc3, tickersNeeded);
                getAllQuotesData = getAllQuotesTask.Result;

                int[] histLength3 = new int[p_allAssetList.Length];
                for (int i = 0; i < histLength3.Length; i++)
                {
                    histLength3[i] = getAllQuotesData.Item1[i].Count;
                }
                allDataLength = (histLength3.Min() == histLength3.Max()) ? histLength3.Min() : 0;
            }

            IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>> quotes;
            List<SQLab.Controllers.QuickTester.Strategies.DailyData> VIXDailyquotes = null;

            quotes = getAllQuotesData.Item1.ToList().GetRange(0, p_allAssetList.Length - 1);
            VIXDailyquotes = getAllQuotesData.Item1[p_allAssetList.Length - 1];


            Tuple<IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>>, List<SQLab.Controllers.QuickTester.Strategies.DailyData>> dataFromSQServer = Tuple.Create(quotes,VIXDailyquotes);

            return dataFromSQServer;
        }

        public Tuple<DateTime[], double[], Tuple<double[], double[], double[], double[], double[], double>> STCIdata(DateTime[] p_usedDateVec)
        {

            //Downloading historical data from vixcentral.com.
            string webpageHist;
            bool isOk = Utils.DownloadStringWithRetry(out webpageHist, "http://vixcentral.com/historical/?days=100", 3, TimeSpan.FromSeconds(2), true);
            //if (!isOk)
            //    return "Error in historical data";

            //Downloading live data from vixcentral.com.
            string webpageLive;
            bool isOkLive = Utils.DownloadStringWithRetry(out webpageLive, "http://vixcentral.com", 3, TimeSpan.FromSeconds(2), true);
            //if (!isOkLive)
            //    return "Error in live data";

            //Selecting data from live data string.
            string[] tableRows = webpageHist.Split(new string[] { "<tr>", "</tr>" }, StringSplitOptions.RemoveEmptyEntries);
            int nHistoricalRec = tableRows.Length - 2;

            string liveFuturesDataDT = System.String.Empty;
            string liveFuturesDataDate = System.String.Empty;
            string liveFuturesDataTime = System.String.Empty;
            string liveFuturesData = System.String.Empty;
            string prevFuturesData = System.String.Empty;
            string liveFuturesNextExp = System.String.Empty;
            string spotVixData = System.String.Empty;

            int startPosLiveDate = webpageLive.IndexOf("var time_data_var=['") + "var time_data_var=['".Length;
            int startPosLive = webpageLive.IndexOf("var last_data_var=[", startPosLiveDate) + "var last_data_var=[".Length;
            int endPosLive = webpageLive.IndexOf("];last_data_var=clean_array(last_data_var);", startPosLive);
            int startPosPrev = webpageLive.IndexOf("];var previous_close_var=[", endPosLive) + "];var previous_close_var=[".Length;
            int endPosPrev = webpageLive.IndexOf("];var contango_graph_exists=", startPosPrev);
            int nextExpLiveMonth = webpageLive.IndexOf("var mx=['", endPosLive) + "var mx=['".Length;
            int startSpotVix = webpageLive.IndexOf("{id:'VIX_Index',name:'VIX Index',legendIndex:9,lineWidth:2,color:'green',dashStyle:'LongDash',marker:{enabled:false},dataLabels:{enabled:true,align:'left',x:5,y:4,formatter:function(){if(this.point.x==this.series.data.length-1){return Highcharts.numberFormat(this.y,2);}else{return null;}}},data:[", nextExpLiveMonth) + "{id:'VIX_Index',name:'VIX Index',legendIndex:9,lineWidth:2,color:'green',dashStyle:'LongDash',marker:{enabled:false},dataLabels:{enabled:true,align:'left',x:5,y:4,formatter:function(){if(this.point.x==this.series.data.length-1){return Highcharts.numberFormat(this.y,2);}else{return null;}}},data:[".Length;
            int endSpotVix = webpageLive.IndexOf("]},{id:'VXV_Index',name:'VXV Index',legendIndex:10,lineWidth:2", startSpotVix);
            liveFuturesDataDT = webpageLive.Substring(startPosLiveDate, 16);
            liveFuturesNextExp = webpageLive.Substring(nextExpLiveMonth, 3);
            liveFuturesData = webpageLive.Substring(startPosLive, endPosLive - startPosLive);
            prevFuturesData = webpageLive.Substring(startPosPrev, endPosPrev - startPosPrev);
            spotVixData = webpageLive.Substring(startSpotVix, endSpotVix - startSpotVix);

            liveFuturesDataDate = liveFuturesDataDT.Substring(0, 10);
            liveFuturesDataTime = liveFuturesDataDT.Substring(11, 5) + " EST";

            string[] liveFuturesPrices = liveFuturesData.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            int lengthLiveFuturesPrices = liveFuturesPrices.Length;
            string[] prevFuturesPrices = prevFuturesData.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            string[] spotVixPrices = spotVixData.Split(new string[] { "," }, StringSplitOptions.RemoveEmptyEntries);
            double spotVixValue = Double.Parse(spotVixPrices[0]);

            string[] monthsNumList = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            int monthsNum = Array.IndexOf(monthsNumList, liveFuturesNextExp) + 1;


            DateTime liveDateTime;
            string liveDate = System.String.Empty;
            liveDateTime = DateTime.Parse(liveFuturesDataDate);
            liveDate = liveDateTime.ToString("yyyy-MM-dd");


            string[] firstTableCells = tableRows[2].Split(new string[] { "<td>", "</td>" }, StringSplitOptions.RemoveEmptyEntries);
            DateTime histStartDay;
            string histStartDate = System.String.Empty;
            histStartDay = DateTime.Parse(firstTableCells[0]);
            histStartDate = histStartDay.ToString("yyyy-MM-dd");
            bool isExtraDay = !string.Equals(liveDate, histStartDate);

            //Sorting historical data.
            int nRec = (isExtraDay) ? nHistoricalRec + 1 : nHistoricalRec;
            VixCentralRec[] vixCentralRec = new VixCentralRec[nRec - 2];


            for (int iRows = 2; iRows < tableRows.Length - 2; iRows++)
            {
                string[] tableCells = tableRows[iRows].Split(new string[] { "<td>", "</td>" }, StringSplitOptions.RemoveEmptyEntries);
                int iRec = (isExtraDay) ? iRows - 1 : iRows - 2;
                vixCentralRec[iRec].Date = DateTime.Parse(tableCells[0]);
                vixCentralRec[iRec].FirstMonth = Int32.Parse(tableCells[1]);
                vixCentralRec[iRec].F1 = Double.Parse(tableCells[2]);
                vixCentralRec[iRec].F2 = Double.Parse(tableCells[3]);
                vixCentralRec[iRec].F3 = Double.Parse(tableCells[4]);
                vixCentralRec[iRec].F4 = Double.Parse(tableCells[5]);
                vixCentralRec[iRec].F5 = Double.Parse(tableCells[6]);
                vixCentralRec[iRec].F6 = Double.Parse(tableCells[7]);
                vixCentralRec[iRec].F7 = Double.Parse(tableCells[8]);
                vixCentralRec[iRec].F8 = (tableCells[9] == "0") ? vixCentralRec[iRec].F7 : Double.Parse(tableCells[9]);
                vixCentralRec[iRec].STCont = vixCentralRec[iRec].F2 / vixCentralRec[iRec].F1 - 1;
                vixCentralRec[iRec].LTCont = vixCentralRec[iRec].F7 / vixCentralRec[iRec].F4 - 1;
            }

            if (isExtraDay)
            {
                vixCentralRec[0].Date = DateTime.Parse(liveDate);
                vixCentralRec[0].FirstMonth = monthsNum;
                vixCentralRec[0].F1 = Double.Parse(liveFuturesPrices[0]);
                vixCentralRec[0].F2 = Double.Parse(liveFuturesPrices[1]);
                vixCentralRec[0].F3 = Double.Parse(liveFuturesPrices[2]);
                vixCentralRec[0].F4 = Double.Parse(liveFuturesPrices[3]);
                vixCentralRec[0].F5 = Double.Parse(liveFuturesPrices[4]);
                vixCentralRec[0].F6 = Double.Parse(liveFuturesPrices[5]);
                vixCentralRec[0].F7 = Double.Parse(liveFuturesPrices[6]);
                vixCentralRec[0].F8 = (lengthLiveFuturesPrices == 8) ? Double.Parse(liveFuturesPrices[7]) : 0;
                vixCentralRec[0].STCont = vixCentralRec[0].F2 / vixCentralRec[0].F1 - 1;
                vixCentralRec[0].LTCont = vixCentralRec[0].F7 / vixCentralRec[0].F4 - 1;

            }

            //Calculating futures expiration dates.

            var firstDataDay = vixCentralRec[nRec - 3].Date;
            int firstDataYear = firstDataDay.Year;
            string firstData = firstDataDay.ToString("yyyy-MM-dd");

            var lastDataDay = vixCentralRec[0].Date;
            int lastDataYear = lastDataDay.Year;
            string lastData = lastDataDay.ToString("yyyy-MM-dd");

            var lengthExps = (lastDataYear - firstDataYear + 2) * 12;
            int[,] expDatesDat = new int[lengthExps, 2];

            expDatesDat[0, 0] = lastDataYear + 1;
            expDatesDat[0, 1] = 12;

            for (int iRows = 1; iRows < expDatesDat.GetLength(0); iRows++)
            {
                decimal f = iRows / 12;
                expDatesDat[iRows, 0] = lastDataYear - Decimal.ToInt32(Math.Floor(f)) + 1;
                expDatesDat[iRows, 1] = 12 - iRows % 12;
            }

            DateTime[] expDates = new DateTime[expDatesDat.GetLength(0)];
            for (int iRows = 0; iRows < expDates.Length; iRows++)
            {
                DateTime thirdFriday = new DateTime(expDatesDat[iRows, 0], expDatesDat[iRows, 1], 15);
                while (thirdFriday.DayOfWeek != DayOfWeek.Friday)
                {
                    thirdFriday = thirdFriday.AddDays(1);
                }
                expDates[iRows] = thirdFriday.AddDays(-30);
                if (expDates[iRows] == DateTime.Parse("2014-03-19"))
                {
                    expDates[iRows] = DateTime.Parse("2014-03-18");
                }
            }

            //Calculating number of calendar days until expirations.
            DateTime[] dateVixVec = new DateTime[vixCentralRec.Length];
            for (int iRec = 0; iRec < vixCentralRec.Length; iRec++)
            {
                int index1 = Array.FindIndex(expDates, item => item <= vixCentralRec[iRec].Date);
                vixCentralRec[iRec].NextExpiryDate = expDates[index1 - 1];
                vixCentralRec[iRec].F1expDays = (expDates[index1 - 1] - vixCentralRec[iRec].Date).Days;
                vixCentralRec[iRec].F2expDays = (expDates[index1 - 2] - vixCentralRec[iRec].Date).Days;
                vixCentralRec[iRec].F3expDays = (expDates[index1 - 3] - vixCentralRec[iRec].Date).Days;
                vixCentralRec[iRec].F4expDays = (expDates[index1 - 4] - vixCentralRec[iRec].Date).Days;
                vixCentralRec[iRec].F5expDays = (expDates[index1 - 5] - vixCentralRec[iRec].Date).Days;
                vixCentralRec[iRec].F6expDays = (expDates[index1 - 6] - vixCentralRec[iRec].Date).Days;
                vixCentralRec[iRec].F7expDays = (expDates[index1 - 7] - vixCentralRec[iRec].Date).Days;
                vixCentralRec[iRec].F8expDays = (vixCentralRec[0].F8 > 0) ? (expDates[index1 - 8] - vixCentralRec[iRec].Date).Days : 0;
                dateVixVec[iRec] = vixCentralRec[iRec].Date;
            }

            int[] usedDateIndexVec = new int[p_usedDateVec.Length];
            for (int iRows = 0; iRows < usedDateIndexVec.Length; iRows++)
            {
                usedDateIndexVec[iRows] = Array.FindIndex(dateVixVec, item => item <= p_usedDateVec[iRows]);
            }
            DateTime[] stciDateVec = new DateTime[15];
            double[] stciValue = new double[15];
            for (int iRow = 0; iRow < stciDateVec.Length; iRow++)
            {
                stciDateVec[iRow] = vixCentralRec[usedDateIndexVec[iRow]].Date;
                if (vixCentralRec[usedDateIndexVec[iRow]].F1expDays > 5)
                {
                    stciValue[iRow] = vixCentralRec[usedDateIndexVec[iRow]].STCont;
                }
                else
                {
                    stciValue[iRow] = vixCentralRec[usedDateIndexVec[iRow]].F3/ vixCentralRec[usedDateIndexVec[iRow]].F2-1;
                }
            }

            //string ret = Processing(vixCentralRec, expDates, liveDate, liveFuturesDataTime);

            //Creating the current data array (prices and spreads).
            double[] currData = new double[28];
            currData[0] = vixCentralRec[0].F1;
            currData[1] = vixCentralRec[0].F2;
            currData[2] = vixCentralRec[0].F3;
            currData[3] = vixCentralRec[0].F4;
            currData[4] = vixCentralRec[0].F5;
            currData[5] = vixCentralRec[0].F6;
            currData[6] = vixCentralRec[0].F7;
            currData[7] = vixCentralRec[0].F8;
            currData[8] = vixCentralRec[0].STCont;
            currData[9] = vixCentralRec[0].LTCont;
            currData[10] = vixCentralRec[0].F2 - vixCentralRec[0].F1;
            currData[11] = vixCentralRec[0].F3 - vixCentralRec[0].F2;
            currData[12] = vixCentralRec[0].F4 - vixCentralRec[0].F3;
            currData[13] = vixCentralRec[0].F5 - vixCentralRec[0].F4;
            currData[14] = vixCentralRec[0].F6 - vixCentralRec[0].F5;
            currData[15] = vixCentralRec[0].F7 - vixCentralRec[0].F6;
            currData[16] = (vixCentralRec[0].F8 > 0) ? vixCentralRec[0].F8 - vixCentralRec[0].F7 : 0;
            currData[17] = vixCentralRec[0].F7 - vixCentralRec[0].F4;
            currData[18] = (vixCentralRec[0].F7 - vixCentralRec[0].F4) / 3;
            currData[19] = vixCentralRec[0].F2 / vixCentralRec[0].F1 - 1;
            currData[20] = vixCentralRec[0].F3 / vixCentralRec[0].F2 - 1;
            currData[21] = vixCentralRec[0].F4 / vixCentralRec[0].F3 - 1;
            currData[22] = vixCentralRec[0].F5 / vixCentralRec[0].F4 - 1;
            currData[23] = vixCentralRec[0].F6 / vixCentralRec[0].F5 - 1;
            currData[24] = vixCentralRec[0].F7 / vixCentralRec[0].F6 - 1;
            currData[25] = (vixCentralRec[0].F8 > 0) ? vixCentralRec[0].F8 / vixCentralRec[0].F7 - 1 : 0;
            currData[26] = vixCentralRec[0].F7 / vixCentralRec[0].F4 - 1;
            currData[27] = (vixCentralRec[0].F7 / vixCentralRec[0].F4 - 1) / 3;

            //Creating the current days to expirations array.
            double[] currDataDays = new double[17];
            currDataDays[0] = vixCentralRec[0].F1expDays;
            currDataDays[1] = vixCentralRec[0].F2expDays;
            currDataDays[2] = vixCentralRec[0].F3expDays;
            currDataDays[3] = vixCentralRec[0].F4expDays;
            currDataDays[4] = vixCentralRec[0].F5expDays;
            currDataDays[5] = vixCentralRec[0].F6expDays;
            currDataDays[6] = vixCentralRec[0].F7expDays;
            currDataDays[7] = (vixCentralRec[0].F8 > 0) ? vixCentralRec[0].F8expDays : 0;
            currDataDays[8] = vixCentralRec[0].F1expDays;
            currDataDays[9] = vixCentralRec[0].F4expDays;
            currDataDays[10] = vixCentralRec[0].F1expDays;
            currDataDays[11] = vixCentralRec[0].F2expDays;
            currDataDays[12] = vixCentralRec[0].F3expDays;
            currDataDays[13] = vixCentralRec[0].F4expDays;
            currDataDays[14] = vixCentralRec[0].F5expDays;
            currDataDays[15] = vixCentralRec[0].F6expDays;
            currDataDays[16] = (vixCentralRec[0].F8 > 0) ? vixCentralRec[0].F7expDays : 0;

            //Creating the data array of previous day (prices and spreads).
            double[] prevData = new double[17];
            prevData[0] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F1 : vixCentralRec[1].F2;
            prevData[1] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F2 : vixCentralRec[1].F3;
            prevData[2] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F3 : vixCentralRec[1].F4;
            prevData[3] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F4 : vixCentralRec[1].F5;
            prevData[4] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F5 : vixCentralRec[1].F6;
            prevData[5] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F6 : vixCentralRec[1].F7;
            prevData[6] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F7 : vixCentralRec[1].F8;
            prevData[7] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? ((vixCentralRec[0].F8 > 0) ? vixCentralRec[1].F8 : 0) : 0;
            prevData[8] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].STCont : vixCentralRec[1].F3 / vixCentralRec[1].F2 - 1;
            prevData[9] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].LTCont : vixCentralRec[1].F8 / vixCentralRec[1].F5 - 1;
            prevData[10] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F2 - vixCentralRec[1].F1 : vixCentralRec[1].F3 - vixCentralRec[1].F2;
            prevData[11] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F3 - vixCentralRec[1].F2 : vixCentralRec[1].F4 - vixCentralRec[1].F3;
            prevData[12] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F4 - vixCentralRec[1].F3 : vixCentralRec[1].F5 - vixCentralRec[1].F4;
            prevData[13] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F5 - vixCentralRec[1].F4 : vixCentralRec[1].F6 - vixCentralRec[1].F5;
            prevData[14] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F6 - vixCentralRec[1].F5 : vixCentralRec[1].F7 - vixCentralRec[1].F6;
            prevData[15] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? vixCentralRec[1].F7 - vixCentralRec[1].F6 : vixCentralRec[1].F8 - vixCentralRec[1].F7;
            prevData[16] = (vixCentralRec[0].F1expDays - vixCentralRec[1].F1expDays <= 0) ? ((vixCentralRec[0].F8 > 0) ? vixCentralRec[1].F8 - vixCentralRec[1].F7 : 0) : 0;

            //Creating the difference of current and previous data array (prices and spreads).
            double[] currDataDiff = new double[17];
            for (int iRow = 0; iRow < currDataDiff.Length; iRow++)
            {
                currDataDiff[iRow] = currData[iRow] - prevData[iRow];
            }

            //Creating the %change of current and previous data array (prices and spreads).
            double[] currDataPercCh = new double[17];
            for (int iRow = 0; iRow < currDataPercCh.Length; iRow++)
            {
                currDataPercCh[iRow] = (prevData[iRow] == 0) ? 0 : (currData[iRow] / prevData[iRow] - 1);
            }

            Tuple<double[], double[], double[], double[], double[], double> vixCont = Tuple.Create(currData, currDataDays, prevData, currDataDiff, currDataPercCh, spotVixValue);

            Tuple<DateTime[], double[], Tuple<double[], double[], double[], double[], double[], double>> stciResults = Tuple.Create(stciDateVec, stciValue, vixCont);
            return stciResults;


        }


        public string GetStr()
        {
            //Defining asset lists.
            string[] allAssetList = new string[] { "VXX", "TQQQ", "UPRO", "SVXY", "TMV", "UCO", "UGAZ" };
            string[] allAssetListVIX = new string[] { "VXX", "TQQQ", "UPRO", "SVXY", "TMV", "UCO", "UGAZ", "^VIX" };

            double[] bullishWeights = new double[] { -0.1, 0.3, 0.3, 0.2, -0.1, -0.075, -0.05 };
            double[] bearishWeights = new double[] { 1, 0, 0, 0, 0, 0, 0 };
            double[] eventMultiplicator = new double[] { 0.5, 1, 1, 0.85, 0.85, 0.7, 0.7 };
            double[] stciThresholds = new double[] { 0.02, 0.09, 0.075 };


            string gchGSheetRefPos = "https://sheets.googleapis.com/v4/spreadsheets/1OZV2MqNJAep9SV1p1YribbHYiYoI7Qz9OjQutV6qJt4/values/A1:Z2000?key=";
            string gchGSheet2RefPos = "https://docs.google.com/spreadsheets/d/1OZV2MqNJAep9SV1p1YribbHYiYoI7Qz9OjQutV6qJt4/edit?usp=sharing";
            string gchGSheetRef = "https://sheets.googleapis.com/v4/spreadsheets/1QjGsXw6YxPT0He5kE4YJ5o52ZCnX7cLA5N-V3Ng1juA/values/A1:Z2000?key=";
            string gchGSheet2Ref = "https://docs.google.com/spreadsheets/d/1QjGsXw6YxPT0He5kE4YJ5o52ZCnX7cLA5N-V3Ng1juA/edit#gid=0";
            string gchGDocRef = "https://docs.google.com/document/d/1q2nSfQUos93q4-dd0ILjTrlvtiQKnwlsg3zN1_0_lyI/edit?usp=sharing";


            string usedGSheetRef = gchGSheetRef;
            string usedGSheet2Ref = gchGSheet2Ref;
            string usedGDocRef = gchGDocRef;
            string usedGSheetRefPos = gchGSheetRefPos;
            string usedGSheet2RefPos = gchGSheet2RefPos;


            //Collecting and splitting price data got from SQL Server
            Tuple<IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>>, List<SQLab.Controllers.QuickTester.Strategies.DailyData>> quotesDataAll = DataSQDBG(allAssetListVIX);
            IList<List<SQLab.Controllers.QuickTester.Strategies.DailyData>> quotesData = quotesDataAll.Item1;
            List<SQLab.Controllers.QuickTester.Strategies.DailyData> VIXQuotes = quotesDataAll.Item2;

            //Get, split and convert GSheet data
            var gSheetReadResultPos = UberRenewedGoogleApiGsheet1(usedGSheetRefPos);
            string gSheetStringPos = ((Microsoft.AspNetCore.Mvc.ContentResult)gSheetReadResultPos).Content;

            Tuple<double[], DateTime[], string[,], int[], int[], int[], string[]> gSheetResToFinCalc = GSheetConverter(gSheetStringPos, allAssetList);


            //Request time (UTC)
            DateTime liveDateTime = DateTime.UtcNow;
            string liveDate = System.String.Empty;
            liveDate = liveDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            DateTime timeNowET = Utils.ConvertTimeFromUtcToEt(liveDateTime);
            string liveDateString = "Request time (UTC): " + liveDate;

            //Last data time (UTC)
            string lastDataTime = (quotesData[0][quotesData[0].Count - 1].Date.Date == liveDateTime.Date & timeNowET.TimeOfDay <= new DateTime(2000, 1, 1, 16, 15, 0).TimeOfDay) ? "Live data at " + liveDateTime.ToString("yyyy-MM-dd HH:mm:ss") : "Close price on " + quotesData[0][quotesData[0].Count - 1].Date.ToString("yyyy-MM-dd");
            string lastDataTimeString = "Last data time (UTC): " + lastDataTime;
            DateTime[] usedDateVec = new DateTime[15];
            for (int iRows = 0; iRows < usedDateVec.Length; iRows++)
            {
                usedDateVec[iRows] = quotesData[0][quotesData[0].Count - iRows - 1].Date.Date;
            }
            double usedMDate = (usedDateVec[0] - new DateTime(1900, 1, 1)).TotalDays + 693962;


            Tuple<DateTime[], double[], Tuple<double[], double[], double[], double[], double[], double>> STCId = STCIdata(usedDateVec);
            Tuple<double[], double[], double[], double[], double[], double> vixCont = STCId.Item3;
            double[] currDataVix = vixCont.Item1;
            double[] currDataDaysVix = vixCont.Item2;
            double[] prevDataVix = vixCont.Item3;
            double[] currDataDiffVix = vixCont.Item4;
            double[] currDataPercChVix = vixCont.Item5;
            double spotVixValue = vixCont.Item6;

            double[] spotVixValueDb = new double[STCId.Item1.Length];
            for (int iRows = 0; iRows < spotVixValueDb.Length; iRows++)
            {
                spotVixValueDb[iRows]= VIXQuotes[VIXQuotes.Count - iRows - 1].AdjClosePrice;
            }

            double[] vixLeverage = new double[STCId.Item1.Length];
            for (int iRows = 0; iRows < vixLeverage.Length; iRows++)
            {
                if (spotVixValueDb[iRows] >= 30)
                {
                    vixLeverage[iRows] = 0.1;
                }
                else if (spotVixValueDb[iRows] >= 21)
                {
                    vixLeverage[iRows] = 1 - (spotVixValueDb[iRows] - 21) * 0.1;
                }
                else
                {
                    vixLeverage[iRows] = 1;
                }
            }

            int[] pastDataResIndexVec = new int[STCId.Item1.Length];
            for (int iRows = 0; iRows < pastDataResIndexVec.Length; iRows++)
            {
                pastDataResIndexVec[iRows] = Array.FindIndex(gSheetResToFinCalc.Item2, item => item >= STCId.Item1[iRows]);
            }


            int[] nextDataResIndexVec = new int[15];
            nextDataResIndexVec[0] = pastDataResIndexVec[0] + 1;
            for (int iRows = 1; iRows < nextDataResIndexVec.Length; iRows++)
            {
                nextDataResIndexVec[iRows] = nextDataResIndexVec[iRows - 1] + 1;
            }

            DateTime[] prevDateVec = new DateTime[pastDataResIndexVec.Length];
            for (int iRows = 0; iRows < prevDateVec.Length; iRows++)
            {
                prevDateVec[iRows] = gSheetResToFinCalc.Item2[pastDataResIndexVec[iRows] + 1];
            }

            DateTime[] nextDateVec = new DateTime[nextDataResIndexVec.Length];
            for (int iRows = 0; iRows < nextDateVec.Length; iRows++)
            {
                nextDateVec[iRows] = gSheetResToFinCalc.Item2[nextDataResIndexVec[iRows] + 1];
            }


            int[] eventFinalSignal = new int[pastDataResIndexVec.Length];
            for (int iRows = 0; iRows < eventFinalSignal.Length; iRows++)
            {
                eventFinalSignal[iRows] = Convert.ToInt32(gSheetResToFinCalc.Item7[pastDataResIndexVec[iRows] + 1]);
            }

            int[] nextEventFinalSignal = new int[nextDataResIndexVec.Length];
            for (int iRows = 0; iRows < nextEventFinalSignal.Length; iRows++)
            {
                nextEventFinalSignal[iRows] = Convert.ToInt32(gSheetResToFinCalc.Item7[nextDataResIndexVec[iRows] + 1]);
            }

            int[] eventCode = new int[pastDataResIndexVec.Length];
            for (int iRows = 0; iRows < eventCode.Length; iRows++)
            {
                eventCode[iRows] = Convert.ToInt32(gSheetResToFinCalc.Item4[pastDataResIndexVec[iRows] + 1]);
            }

            int[] nextEventCode = new int[nextDataResIndexVec.Length];
            for (int iRows = 0; iRows < nextEventCode.Length; iRows++)
            {
                nextEventCode[iRows] = Convert.ToInt32(gSheetResToFinCalc.Item4[nextDataResIndexVec[iRows] + 1]);
            }

            double[] eventFinalWeightedSignal = new double[pastDataResIndexVec.Length];
            for (int iRows = 0; iRows < eventFinalWeightedSignal.Length; iRows++)
            {
                eventFinalWeightedSignal[iRows] = Convert.ToInt32(gSheetResToFinCalc.Item7[pastDataResIndexVec[iRows] + 1]) * eventMultiplicator[eventCode[iRows]];
            }

            double[] nextEventFinalWeightedSignal = new double[nextDataResIndexVec.Length];
            for (int iRows = 0; iRows < nextEventFinalWeightedSignal.Length; iRows++)
            {
                nextEventFinalWeightedSignal[iRows] = Convert.ToInt32(gSheetResToFinCalc.Item7[nextDataResIndexVec[iRows] + 1]) * eventMultiplicator[nextEventCode[iRows]];
            }

            double[] finalWeightMultiplier = new double[pastDataResIndexVec.Length];
            double[] finalWeightMultiplierVIX = new double[pastDataResIndexVec.Length];
            int[] eventCodeFinal = new int[pastDataResIndexVec.Length];
            for (int iRows = 0; iRows < finalWeightMultiplier.Length; iRows++)
            {
                if ((eventCode[iRows] <= 4 && eventCode[iRows] > 0) || (eventCode[iRows] == 5 && STCId.Item2[iRows] >= stciThresholds[0]) || (eventCode[iRows] == 6 && STCId.Item2[iRows] <= stciThresholds[1]))
                {
                    finalWeightMultiplier[iRows] = eventFinalWeightedSignal[iRows];
                    finalWeightMultiplierVIX[iRows] = finalWeightMultiplier[iRows] *vixLeverage[iRows];
                    eventCodeFinal[iRows] = eventCode[iRows];
                }
                else if (eventCode[iRows] == 0 && STCId.Item2[iRows] >= stciThresholds[2])
                {
                    finalWeightMultiplier[iRows] = eventMultiplicator[0];
                    finalWeightMultiplierVIX[iRows] = finalWeightMultiplier[iRows] * vixLeverage[iRows];
                    eventCodeFinal[iRows] = 7;
                }
                else if ((eventCode[iRows] == 5 && STCId.Item2[iRows] < stciThresholds[0]) || (eventCode[iRows] == 6 && STCId.Item2[iRows] > stciThresholds[1]))
                {
                    finalWeightMultiplier[iRows] = 0;
                    finalWeightMultiplierVIX[iRows] = finalWeightMultiplier[iRows] * vixLeverage[iRows];
                    eventCodeFinal[iRows] = 9;
                }
                else
                {
                    finalWeightMultiplier[iRows] = 0;
                    finalWeightMultiplierVIX[iRows] = finalWeightMultiplier[iRows] * vixLeverage[iRows];
                    eventCodeFinal[iRows] = 8;
                }
            }


            int currEventSignal = eventFinalSignal[0];
            int currEventCodeOriginal = eventCode[0];
            int currEventCode = 0;
            if (eventCode[0] <= 6 && eventCode[0] > 0)
            {
                currEventCode = eventCode[0];
            }
            else if (STCId.Item2[0] >= stciThresholds[2])
            {
                currEventCode = 7;
            }
            else
            {
                currEventCode = 8;
            }




            int[] prevEventCodeMod = new int[usedDateVec.Length];
            for (int iRows = 0; iRows < prevEventCodeMod.Length; iRows++)
            {
                if (eventCode[iRows] <= 6 && eventCode[iRows] > 0)
                {
                    prevEventCodeMod[iRows] = eventCode[iRows];
                }
                else if (STCId.Item2[iRows] >= stciThresholds[2])
                {
                    prevEventCodeMod[iRows] = 7;
                }
                else
                {
                    prevEventCodeMod[iRows] = 8;
                }
            }

            string[] prevEventNamesMod = new string[prevEventCodeMod.Length];
            for (int iRows = 0; iRows < prevEventCodeMod.Length; iRows++)
            {
                switch (prevEventCodeMod[iRows])
                {
                    case 1:
                        prevEventNamesMod[iRows] = "FOMC Bullish Day";
                        break;
                    case 2:
                        prevEventNamesMod[iRows] = "FOMC Bearish Day";
                        break;
                    case 3:
                        prevEventNamesMod[iRows] = "Holiday Bullish Day";
                        break;
                    case 4:
                        prevEventNamesMod[iRows] = "Holiday Bearish Day";
                        break;
                    case 5:
                        prevEventNamesMod[iRows] = "Other Bullish Event Day";
                        break;
                    case 6:
                        prevEventNamesMod[iRows] = "Other Bearish Event Day";
                        break;
                    case 7:
                        prevEventNamesMod[iRows] = "Non-Event Day";
                        break;
                    case 8:
                        prevEventNamesMod[iRows] = "Non-Event Day";
                        break;
                }
            }

            string[] nextEventNames = new string[nextEventCode.Length];
            string[] nextEventColors = new string[nextEventCode.Length];
            for (int iRows = 0; iRows < nextEventCode.Length; iRows++)
            {
                switch (nextEventCode[iRows])
                {
                    case 1:
                        nextEventNames[iRows] = "FOMC Bullish Day";
                        nextEventColors[iRows] = "32CD32";
                        break;
                    case 2:
                        nextEventNames[iRows] = "FOMC Bearish Day";
                        nextEventColors[iRows] = "c24f4f";
                        break;
                    case 3:
                        nextEventNames[iRows] = "Holiday Bullish Day";
                        nextEventColors[iRows] = "7CFC00";
                        break;
                    case 4:
                        nextEventNames[iRows] = "Holiday Bearish Day";
                        nextEventColors[iRows] = "d46a6a";
                        break;
                    case 5:
                        nextEventNames[iRows] = "Other Bullish Event Day";
                        nextEventColors[iRows] = "00FA9A";
                        break;
                    case 6:
                        nextEventNames[iRows] = "Other Bearish Event Day";
                        nextEventColors[iRows] = "ed8c8c";
                        break;
                    case 0:
                        nextEventNames[iRows] = "Non-Event Day";
                        nextEventColors[iRows] = "FFFF00";
                        break;
                }
            }


            double currWeightedSignal = eventFinalWeightedSignal[0];
            double currSTCI = STCId.Item2[0];
            double currFinalWeightMultiplier = finalWeightMultiplierVIX[0];

            string currEventName = "";
            switch (currEventCode)
            {
                case 1:
                    currEventName = "FOMC Bullish Day";
                    break;
                case 2:
                    currEventName = "FOMC Bearish Day";
                    break;
                case 3:
                    currEventName = "Holiday Bullish Day";
                    break;
                case 4:
                    currEventName = "Holiday Bearish Day";
                    break;
                case 5:
                    currEventName = "Other Bullish Event Day";
                    break;
                case 6:
                    currEventName = "Other Bearish Event Day";
                    break;
                case 7:
                    currEventName = "STCI Bullish Day";
                    break;
                case 8:
                    currEventName = "STCI Neutral Day";
                    break;

            }



            //Current PV, Number of current and required shares
            DateTime startMatlabDate = DateTime.ParseExact("1900/01/01", "yyyy/MM/dd", CultureInfo.InvariantCulture);

            DateTime nextTradingDay = startMatlabDate.AddDays(gSheetResToFinCalc.Item1[pastDataResIndexVec[0] + 1] - 693962);
            string nextTradingDayString = System.String.Empty;
            nextTradingDayString = nextTradingDay.ToString("yyyy-MM-dd");

            DateTime currPosDate = startMatlabDate.AddDays(gSheetResToFinCalc.Item5[0] - 693962);
            string currPosDateString = System.String.Empty;
            currPosDateString = currPosDate.ToString("yyyy-MM-dd");

            double currPV;
            double prevPV;
            int[] currPosInt = new int[allAssetList.Length + 1];
            


            double[] currPosValue = new double[allAssetList.Length + 1];
            double[] prevPosValue = new double[allAssetList.Length + 1];
            for (int jCols = 0; jCols < currPosValue.Length - 1; jCols++)
            {
                currPosInt[jCols] = gSheetResToFinCalc.Item6[jCols];
                currPosValue[jCols] = quotesData[jCols][quotesData[0].Count - 1].AdjClosePrice * currPosInt[jCols];
                prevPosValue[jCols] = quotesData[jCols][quotesData[0].Count - 2].AdjClosePrice * currPosInt[jCols];
            }

            currPosInt[currPosInt.Length - 1] = gSheetResToFinCalc.Item5[1];
            currPosValue[currPosValue.Length - 1] = gSheetResToFinCalc.Item5[1];
            prevPosValue[currPosValue.Length - 1] = gSheetResToFinCalc.Item5[1];
            currPV = Math.Round(currPosValue.Sum());
            prevPV = Math.Round(prevPosValue.Sum());
            double dailyProf = currPV - prevPV;
            string dailyProfValString = "";
            

            string dailyProfString = "";
            string dailyProfSign = "";
            if (currPosDateString == liveDateTime.ToString("yyyy-MM-dd") && currPosDateString== quotesData[0][quotesData[0].Count - 1].Date.ToString("yyyy-MM-dd") && dailyProf >= 0)
            {
                dailyProfString = "posDaily";
                dailyProfSign = "+$";
                dailyProfValString = dailyProf.ToString("#,##0");

            }
            else if (currPosDateString == liveDateTime.ToString("yyyy-MM-dd") && currPosDateString == quotesData[0][quotesData[0].Count - 1].Date.ToString("yyyy-MM-dd") && dailyProf < 0)
            {
                dailyProfString = "negDaily";
                dailyProfSign = "-$";
                dailyProfValString = (-dailyProf).ToString("#,##0");
            }
            else
            {
                dailyProfString = "notDaily";
                dailyProfSign = "N/A";
                dailyProfValString = "";
            }
             

            double[] nextPosValue = new double[allAssetList.Length + 1];
            for (int jCols = 0; jCols < nextPosValue.Length - 1; jCols++)
            {
                if (finalWeightMultiplierVIX[0] > 0)
                {
                    nextPosValue[jCols] = currPV * finalWeightMultiplierVIX[0] * bullishWeights[jCols];
                }
                else if (finalWeightMultiplierVIX[0] < 0)
                {
                    nextPosValue[jCols] = currPV * finalWeightMultiplierVIX[0] * bearishWeights[jCols] * (-1);
                }
            }

            nextPosValue[nextPosValue.Length - 1] = currPV - nextPosValue.Take(nextPosValue.Length - 1).ToArray().Sum();

            double[] nextPosInt = new double[nextPosValue.Length];
            for (int jCols = 0; jCols < nextPosInt.Length - 1; jCols++)
            {
                nextPosInt[jCols] = nextPosValue[jCols] / quotesData[jCols][quotesData[0].Count - 1].AdjClosePrice;
            }
            nextPosInt[nextPosInt.Length - 1] = nextPosValue[nextPosInt.Length - 1];

            double[] posValueDiff = new double[allAssetList.Length + 1];
            for (int jCols = 0; jCols < posValueDiff.Length; jCols++)
            {
                posValueDiff[jCols] = nextPosValue[jCols] - currPosValue[jCols];
            }

            double[] posIntDiff = new double[allAssetList.Length + 1];
            for (int jCols = 0; jCols < posIntDiff.Length; jCols++)
            {
                posIntDiff[jCols] = nextPosInt[jCols] - currPosInt[jCols];
            }


            //Previous event and color arrays
            string[] prevEventNames = new string[pastDataResIndexVec.Length];
            string[] prevEventColors = new string[pastDataResIndexVec.Length];
            for (int iRows = 0; iRows < prevEventNames.Length; iRows++)
            {
                switch (eventCodeFinal[iRows])
                {
                    case 1:
                        prevEventNames[iRows] = "FOMC Bullish Day";
                        prevEventColors[iRows] = "32CD32";
                        break;
                    case 2:
                        prevEventNames[iRows] = "FOMC Bearish Day";
                        prevEventColors[iRows] = "c24f4f";
                        break;
                    case 3:
                        prevEventNames[iRows] = "Holiday Bullish Day";
                        prevEventColors[iRows] = "7CFC00";
                        break;
                    case 4:
                        prevEventNames[iRows] = "Holiday Bearish Day";
                        prevEventColors[iRows] = "d46a6a";
                        break;
                    case 5:
                        prevEventNames[iRows] = "Other Bullish Event Day";
                        prevEventColors[iRows] = "00FA9A";
                        break;
                    case 6:
                        prevEventNames[iRows] = "Other Bearish Event Day";
                        prevEventColors[iRows] = "ed8c8c";
                        break;
                    case 7:
                        prevEventNames[iRows] = "STCI Bullish Day";
                        prevEventColors[iRows] = "00FFFF";
                        break;
                    case 8:
                        prevEventNames[iRows] = "STCI Neutral Day";
                        prevEventColors[iRows] = "FFFACD";
                        break;
                    case 9:
                        prevEventNames[iRows] = "Non-Playable Other Event Day";
                        prevEventColors[iRows] = "C0C0C0";
                        break;

                }
            }


            string[,] pastDataMtxToJS = new string[usedDateVec.Length, 16];
            for (int iRows = 0; iRows < pastDataMtxToJS.GetLength(0); iRows++)
            {
                pastDataMtxToJS[iRows, 0] = prevDateVec[iRows].ToString("yyyy-MM-dd");
                for (int jCols = 0; jCols < 8; jCols++)
                {
                    pastDataMtxToJS[iRows, jCols + 1] = (gSheetResToFinCalc.Item3[pastDataResIndexVec[iRows] + 1, jCols] == "0") ? "---" : gSheetResToFinCalc.Item3[pastDataResIndexVec[iRows] + 1, jCols];
                }
                pastDataMtxToJS[iRows, 9] = prevEventNamesMod[iRows];
                pastDataMtxToJS[iRows, 10] = eventFinalSignal[iRows].ToString();
                pastDataMtxToJS[iRows, 11] = Math.Round(eventFinalWeightedSignal[iRows] * 100, 2).ToString() + "%";
                pastDataMtxToJS[iRows, 12] = Math.Round(STCId.Item2[iRows] * 100, 2).ToString() + "%";
                pastDataMtxToJS[iRows, 13] = Math.Round(spotVixValueDb[iRows], 2).ToString();
                pastDataMtxToJS[iRows, 14] = prevEventNames[iRows];
                pastDataMtxToJS[iRows, 15] = Math.Round(finalWeightMultiplierVIX[iRows] * 100, 2).ToString() + "%";

            }

            string[,] nextDataMtxToJS = new string[nextDateVec.Length, 12];
            for (int iRows = 0; iRows < nextDataMtxToJS.GetLength(0); iRows++)
            {
                nextDataMtxToJS[iRows, 0] = nextDateVec[iRows].ToString("yyyy-MM-dd");
                for (int jCols = 0; jCols < 8; jCols++)
                {
                    nextDataMtxToJS[iRows, jCols + 1] = (gSheetResToFinCalc.Item3[nextDataResIndexVec[iRows] + 1, jCols] == "0") ? "---" : gSheetResToFinCalc.Item3[nextDataResIndexVec[iRows] + 1, jCols];
                }
                nextDataMtxToJS[iRows, 9] = nextEventNames[iRows];
                nextDataMtxToJS[iRows, 10] = nextEventFinalSignal[iRows].ToString();
                nextDataMtxToJS[iRows, 11] = Math.Round(nextEventFinalWeightedSignal[iRows] * 100, 2).ToString() + "%";

            }



            //AssetPrice Changes in last 20 days to chart
            int assetChartLength = 20;
            string[,] assetChangesMtx = new string[assetChartLength + 1, allAssetList.Length+1];
            for (int iRows = 0; iRows < assetChangesMtx.GetLength(0); iRows++)
            {
                assetChangesMtx[iRows, 0] = quotesData[0][quotesData[0].Count - 1 - assetChartLength + iRows].Date.ToString("yyyy-MM-dd");
                for (int jCols = 0; jCols < assetChangesMtx.GetLength(1) - 1; jCols++)
                {
                    assetChangesMtx[iRows, jCols + 1] = Math.Round((quotesData[jCols][quotesData[jCols].Count - 1 - assetChartLength + iRows].AdjClosePrice / quotesData[jCols][quotesData[jCols].Count - 1 - assetChartLength].AdjClosePrice - 1) * 100.0, 2).ToString() + "%";
                }
            }



            //Creating input string for JavaScript.
            StringBuilder sb = new StringBuilder("{" + Environment.NewLine);
            sb.Append(@"""requestTime"": """ + liveDateString);
            sb.Append(@"""," + Environment.NewLine + @"""lastDataTime"": """ + lastDataTimeString);
            sb.Append(@"""," + Environment.NewLine + @"""currentPV"": """ + currPV.ToString("#,##0"));
            sb.Append(@"""," + Environment.NewLine + @"""dailyProfSig"": """ + dailyProfSign);
            sb.Append(@"""," + Environment.NewLine + @"""dailyProfAbs"": """ + dailyProfValString);
            sb.Append(@"""," + Environment.NewLine + @"""dailyProfString"": """ + dailyProfString);
            sb.Append(@"""," + Environment.NewLine + @"""currentPVDate"": """ + currPosDateString);
            sb.Append(@"""," + Environment.NewLine + @"""gDocRef"": """ + usedGDocRef);
            sb.Append(@"""," + Environment.NewLine + @"""gSheetRef"": """ + usedGSheet2RefPos);

            sb.Append(@"""," + Environment.NewLine + @"""currentEventSignal"": """ + currEventSignal.ToString());
            sb.Append(@"""," + Environment.NewLine + @"""currentEventCode"": """ + currEventCode.ToString());
            sb.Append(@"""," + Environment.NewLine + @"""currentEventCodeOriginal"": """ + currEventCodeOriginal.ToString());
            sb.Append(@"""," + Environment.NewLine + @"""currentEventName"": """ + currEventName);
            sb.Append(@"""," + Environment.NewLine + @"""currentWeightedSignal"": """ + currWeightedSignal.ToString());
            sb.Append(@"""," + Environment.NewLine + @"""currentSTCI"": """ + Math.Round(currSTCI * 100, 2).ToString() + "%");
            sb.Append(@"""," + Environment.NewLine + @"""currentVIX"": """ + Math.Round(spotVixValueDb[0], 2).ToString());
            sb.Append(@"""," + Environment.NewLine + @"""currentFinalWeightMultiplier"": """ + Math.Abs(Math.Round(currFinalWeightMultiplier * 100, 2)).ToString() + "%");



            sb.Append(@"""," + Environment.NewLine + @"""assetNames"": """);
            for (int i = 0; i < allAssetList.Length - 1; i++)
                sb.Append(allAssetList[i] + ", ");
            sb.Append(allAssetList[allAssetList.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""assetNames2"": """);
            for (int i = 0; i < allAssetList.Length; i++)
                sb.Append(allAssetList[i] + ", ");
            sb.Append("Cash");

            sb.Append(@"""," + Environment.NewLine + @"""currPosNum"": """);
            for (int i = 0; i < currPosInt.Length - 1; i++)
                sb.Append(currPosInt[i].ToString() + ", ");
            sb.Append("$" + Math.Round(currPosInt[currPosInt.Length - 1] / 1000.0).ToString() + "K");

            sb.Append(@"""," + Environment.NewLine + @"""currPosVal"": """);
            for (int i = 0; i < currPosValue.Length - 1; i++)
                sb.Append("$" + Math.Round(currPosValue[i] / 1000).ToString() + "K, ");
            sb.Append("$" + Math.Round(currPosValue[currPosValue.Length - 1] / 1000).ToString() + "K");

            sb.Append(@"""," + Environment.NewLine + @"""nextPosNum"": """);
            for (int i = 0; i < nextPosInt.Length - 1; i++)
                sb.Append(Math.Round(nextPosInt[i]).ToString() + ", ");
            sb.Append("$" + Math.Round(nextPosInt[nextPosInt.Length - 1] / 1000).ToString() + "K");

            sb.Append(@"""," + Environment.NewLine + @"""nextPosVal"": """);
            for (int i = 0; i < nextPosValue.Length - 1; i++)
                sb.Append("$" + Math.Round(nextPosValue[i] / 1000).ToString() + "K, ");
            sb.Append("$" + Math.Round(nextPosValue[nextPosValue.Length - 1] / 1000).ToString() + "K");

            sb.Append(@"""," + Environment.NewLine + @"""posNumDiff"": """);
            for (int i = 0; i < posIntDiff.Length - 1; i++)
                sb.Append(Math.Round(posIntDiff[i]).ToString() + ", ");
            sb.Append("$" + Math.Round(posIntDiff[posIntDiff.Length - 1] / 1000).ToString() + "K");

            sb.Append(@"""," + Environment.NewLine + @"""posValDiff"": """);
            for (int i = 0; i < posValueDiff.Length - 1; i++)
                sb.Append("$" + Math.Round(posValueDiff[i] / 1000).ToString() + "K, ");
            sb.Append("$" + Math.Round(posValueDiff[posValueDiff.Length - 1] / 1000).ToString() + "K");

            sb.Append(@"""," + Environment.NewLine + @"""nextTradingDay"": """ + nextTradingDayString);
            sb.Append(@"""," + Environment.NewLine + @"""currPosDate"": """ + currPosDateString);

            sb.Append(@"""," + Environment.NewLine + @"""prevEventNames"": """);
            for (int i = 0; i < prevEventNames.Length - 1; i++)
                sb.Append(prevEventNames[i] + ", ");
            sb.Append(prevEventNames[prevEventNames.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""prevEventColors"": """);
            for (int i = 0; i < prevEventColors.Length - 1; i++)
                sb.Append(prevEventColors[i] + ", ");
            sb.Append(prevEventColors[prevEventColors.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""nextEventColors"": """);
            for (int i = 0; i < nextEventColors.Length - 1; i++)
                sb.Append(nextEventColors[i] + ", ");
            sb.Append(nextEventColors[nextEventColors.Length - 1]);

            sb.Append(@"""," + Environment.NewLine + @"""pastDataMtxToJS"": """);
            for (int i = 0; i < pastDataMtxToJS.GetLength(0); i++)
            {
                sb.Append("");
                for (int j = 0; j < pastDataMtxToJS.GetLength(1) - 1; j++)
                {
                    sb.Append(pastDataMtxToJS[i, j] + ", ");
                }
                sb.Append(pastDataMtxToJS[i, pastDataMtxToJS.GetLength(1) - 1]);
                if (i < pastDataMtxToJS.GetLength(0) - 1)
                {
                    sb.Append("ß ");
                }
            }

            sb.Append(@"""," + Environment.NewLine + @"""nextDataMtxToJS"": """);
            for (int i = 0; i < nextDataMtxToJS.GetLength(0); i++)
            {
                sb.Append("");
                for (int j = 0; j < nextDataMtxToJS.GetLength(1) - 1; j++)
                {
                    sb.Append(nextDataMtxToJS[i, j] + ", ");
                }
                sb.Append(nextDataMtxToJS[i, nextDataMtxToJS.GetLength(1) - 1]);
                if (i < nextDataMtxToJS.GetLength(0) - 1)
                {
                    sb.Append("ß ");
                }
            }

            sb.Append(@"""," + Environment.NewLine + @"""chartLength"": """ + assetChartLength);


            sb.Append(@"""," + Environment.NewLine + @"""assetChangesToChartMtx"": """);
            for (int i = 0; i < assetChangesMtx.GetLength(0); i++)
            {
                sb.Append("");
                for (int j = 0; j < assetChangesMtx.GetLength(1) - 1; j++)
                {
                    sb.Append(assetChangesMtx[i, j] + ", ");
                }
                sb.Append(assetChangesMtx[i, assetChangesMtx.GetLength(1) - 1]);
                if (i < assetChangesMtx.GetLength(0) - 1)
                {
                    sb.Append("ß ");
                }
            }

            sb.Append(@"""," + Environment.NewLine + @"""currDataVixVec"": """);
            for (int i = 0; i < currDataVix.Length - 1; i++)
                sb.Append(Math.Round(currDataVix[i], 4).ToString() + ", ");
            sb.Append(Math.Round(currDataVix[currDataVix.Length - 1], 4).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""currDataDaysVixVec"": """);
            for (int i = 0; i < currDataDaysVix.Length - 1; i++)
                sb.Append(currDataDaysVix[i].ToString() + ", ");
            sb.Append(currDataDaysVix[currDataDaysVix.Length - 1].ToString());

            sb.Append(@"""," + Environment.NewLine + @"""prevDataVixVec"": """);
            for (int i = 0; i < prevDataVix.Length - 1; i++)
                sb.Append(Math.Round(prevDataVix[i], 4).ToString() + ", ");
            sb.Append(Math.Round(prevDataVix[prevDataVix.Length - 1], 4).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""currDataDiffVixVec"": """);
            for (int i = 0; i < currDataDiffVix.Length - 1; i++)
                sb.Append(Math.Round(currDataDiffVix[i], 4).ToString() + ", ");
            sb.Append(Math.Round(currDataDiffVix[currDataDiffVix.Length - 1], 4).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""currDataPercChVixVec"": """);
            for (int i = 0; i < currDataPercChVix.Length - 1; i++)
                sb.Append(Math.Round(currDataPercChVix[i], 4).ToString() + ", ");
            sb.Append(Math.Round(currDataPercChVix[currDataPercChVix.Length - 1], 4).ToString());

            sb.Append(@"""," + Environment.NewLine + @"""spotVixVec"": """);
            for (int i = 0; i < currDataVix.Length - 1; i++)
                sb.Append(Math.Round(spotVixValue, 4).ToString() + ", ");
            sb.Append(Math.Round(spotVixValue, 4).ToString());

            sb.AppendLine(@"""" + Environment.NewLine + @"}");

            var asdfa = sb.ToString(); //testing created string to JS

            return sb.ToString();

        }


    }
}
