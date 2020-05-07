"use strict";
//As an example, in normal JavaScript, mistyping a variable name creates a new global variable. In strict mode, this will throw an error, making it impossible to accidentally create a global variable.

function onHeadProcessing() {
    console.log('onHeadProcessing()');

    var xmlHttp = new XMLHttpRequest();
    xmlHttp.onreadystatechange = function () {
        if (xmlHttp.readyState == 4 && xmlHttp.status == 200)
            processData(xmlHttp.responseText);
    };
    xmlHttp.open("GET", "/RenewedUberData", true); // true for asynchronous 
    xmlHttp.send(null);
}

function processData(dataStr) {
    //Printing error message from C#.  

    if (dataStr == "Error") {
        var divErrorCont = document.getElementById("idErrorCont");
        divErrorCont.innerHTML = "Error during downloading data. Please, try again later!";
        document.getElementById("errorMessage").style.visibility = "visible";
        document.getElementById("inviCharts").style.visibility = "hidden";

        return;
    }

    document.getElementById("errorMessage").style.display = "none";

    //Splitting data got from C#.
    var data = JSON.parse(dataStr);

    //Creating first rows of webpage.
    var divTitleCont = document.getElementById("idTitleCont");
    var divTimeNow = document.getElementById("idTimeNow");
    var divLiveDataTime = document.getElementById("idLiveDataTime");
    var divCurrentPV = document.getElementById("idCurrentPV");
    var divDailyProfit = document.getElementById("idDailyProfit");
    var divCurrentEvent = document.getElementById("idCurrentEvent");
    var divCLMTString = document.getElementById("idCLMTString");
    var divPosLastString = document.getElementById("idPosLast");
    var divPosFutString = document.getElementById("idPosFut");
    var divVixContString = document.getElementById("idVixCont");
    var divRulesString = document.getElementById("idRules");

       
    divTitleCont.innerHTML = '<small><a href="' + data.gDocRef + '" target="_blank">(Study)</a></small>';
    divTimeNow.innerHTML = data.requestTime;
    divLiveDataTime.innerHTML = data.lastDataTime;
    divCurrentPV.innerHTML = "Current PV: <span class=\"pv\">$" + data.currentPV + "</span> (based on <a href=" + data.gSheetRef + '" target="_blank">these current positions</a> updated for ' + data.currentPVDate + ")";
    if (data.dailyProfSig !== "N/A") { divDailyProfit.innerHTML = "<b>Daily Profit/Loss: <span class=\"" + data.dailyProfString + "\">" + data.dailyProfSig + data.dailyProfAbs + "</span></b>";}
    divCurrentEvent.innerHTML = "Next trading day will be <span class=\"stci\"> " + data.currentEventName + "</span>, <div class=\"tooltip\">used STCI is <span class=\"stci\">" + data.currentSTCI + "</span><span class=\"tooltiptext\">Second (third) month VIX futures divided by front (second) month VIX futures minus 1, with more (less) than 5-days until expiration.</span></div > and used VIX is <span class=\"stci\">"+data.currentVIX+"</span>, thus leverage will be <span class=\"stci\">" + data.currentFinalWeightMultiplier + ".</span >";
    divPosLastString.innerHTML = "Recent Events";
    divPosFutString.innerHTML = "Upcoming Events";
    divVixContString.innerHTML = "VIX Futures Term Structure";
    divRulesString.innerHTML = "<u>Current trading rules:</u> <ul><li>Play with 100% of PV on FOMC days (as this is the strongest part of the strategy), with 85% on Holiday days, with 70% on VIXFUTEX, OPEX, TotM and TotMM days, while with only 50% of PV on pure bullish STCI days. These deleveraging percentages have to be played both on bullish and bearish days.</li><li><ul><li>All of the FOMC and Holiday signals have to be played, regardless the STCI;</li><li>on weaker bullish days (OPEX, VIXFUTEX, TotM and TotMM) play the UberMix basket if and only if the STCI closed above +2% contango (25th percentile) on previous day (so, stay in cash if contango is not big enough);</li><li>on weaker bearish days (OPEX, VIXFUTEX, TotM and TotMM) play long VXX if and only if the STCI closed below +9% contango (75th percentile) on previous day (so, stay in cash if the contango is too deep).</li></ul></li><li>Bullish STCI threshold on non-event days is +7.5%, which is the 67th percentile of historical value of the STCI.</li><li>VIX Based Leverage Indicator: <ul><li>If VIX<21:&emsp; leverage = 100%;</li><li>If 21<=VIX<30:&emsp; leverage = 100%-(VIX-21)*10%;</li><li>If 30<=VIX:&emsp; leverage = 10%.</li></ul></li></ul>";

    creatingTables(data);

    //Setting charts visible after getting data.
    document.getElementById("inviCharts").style.visibility = "visible";
}
function creatingTables(data) {


    //Creating JavaScript data arrays by splitting.
    var assetNames2Array = data.assetNames2.split(", ");
    var currPosNumArray = data.currPosNum.split(", ");
    var currPosValArray = data.currPosVal.split(", ");
    var nextPosNumArray = data.nextPosNum.split(", ");
    var nextPosValArray = data.nextPosVal.split(", ");
    var diffPosNumArray = data.posNumDiff.split(", ");
    var diffPosValArray = data.posValDiff.split(", ");
    var prevEventNames = data.prevEventNames.split(", ");
    var prevEventColors = data.prevEventColors.split(", ");
    var nextEventColors = data.nextEventColors.split(", ");

    var prevPosMtxTemp = data.pastDataMtxToJS.split("ß ");
    var prevPosMtx = new Array();
    for (var i = 0; i < prevPosMtxTemp.length; i++) {
        prevPosMtx[i] = prevPosMtxTemp[i].split(",");
    }

    var nextPosMtxTemp = data.nextDataMtxToJS.split("ß ");
    var nextPosMtx = new Array();
    for (var i = 0; i < nextPosMtxTemp.length; i++) {
        nextPosMtx[i] = nextPosMtxTemp[i].split(",");
    }


    var assChartMtxTemp = data.assetChangesToChartMtx.split("ß ");
    var assChartMtx = new Array();
    for (var i = 0; i < assChartMtxTemp.length; i++) {
        assChartMtx[i] = assChartMtxTemp[i].split(",");
    }


    //Creating the HTML code of tables.
    var currTableMtx = "<table class=\"currData\"><tr align=\"center\"><td bgcolor=\"#66CCFF\"></td>";
    for (var i = 0; i < assetNames2Array.length - 1; i++) {
        currTableMtx += "<td bgcolor=\"#66CCFF\"><a href=https://finance.yahoo.com/quote/" + assetNames2Array[i] + ' target="_blank">' + assetNames2Array[i] + "</a></td>";
    }
    currTableMtx += "<td bgcolor=\"#66CCFF\">" + assetNames2Array[assetNames2Array.length - 1] + "</td>";

    currTableMtx += "</tr > <tr align=\"center\"><td align=\"center\" rowspan=\"2\" bgcolor=\"#FF6633\">" + data.nextTradingDay + "</td>";
    for (var i = 0; i < assetNames2Array.length; i++) {
        currTableMtx += "<td bgcolor=\"#" + prevEventColors[0] + "\">" + nextPosValArray[i] + "</td>";
    }

    currTableMtx += "</tr > <tr>";
    for (var i = 0; i < assetNames2Array.length; i++) {
        currTableMtx += "<td bgcolor=\"#" + prevEventColors[0] + "\">" + nextPosNumArray[i] + "</td>";

    }

    currTableMtx += "</tr > <tr align=\"center\"><td align=\"center\" rowspan=\"2\" bgcolor=\"#FF6633\">" + data.currPosDate + "</td>";
    for (var i = 0; i < assetNames2Array.length; i++) {
        currTableMtx += "<td bgcolor=\"#" + prevEventColors[1] + "\">" + currPosValArray[i] + "</td>";

    }

    currTableMtx += "</tr > <tr>";
    for (var i = 0; i < assetNames2Array.length; i++) {
        currTableMtx += "<td bgcolor=\"#" + prevEventColors[1] + "\">" + currPosNumArray[i] + "</td>";

    }

    currTableMtx += "</tr > <tr align=\"center\"><td align=\"center\" rowspan=\"2\" bgcolor=\"#FF6633\">Change in Positions</td>";
    for (var i = 0; i < assetNames2Array.length; i++) {
        currTableMtx += "<td bgcolor=\"#FFFF00\">" + diffPosValArray[i] + "</td>";
    }

    currTableMtx += "</tr > <tr>";
    for (var i = 0; i < assetNames2Array.length; i++) {
        currTableMtx += "<td bgcolor=\"#FFFF00\">" + diffPosNumArray[i] + "</td>";
    }

    currTableMtx += "</tr></table>";

    var currTableMtx3 = "<table class=\"currData\"><tr align=\"center\"  bgcolor=\"#66CCFF\"><td rowspan=\"3\">Date</td><td colspan=\"8\">Events</td><td rowspan=\"3\">Most Significant Event</td><td rowspan=\"3\">M.S. Event Signal</td><td rowspan=\"3\">M.S. Event Leverage</td><td rowspan=\"3\">Used STCI</td><td rowspan=\"3\">Used VIX</td><td rowspan=\"3\"><b>Played Event</b></td><td rowspan=\"3\"><b>Played Leveraged Signal</b></td></tr><tr align=\"center\" bgcolor=\"#66CCFF\"><td colspan=\"2\">Event 1</td><td colspan=\"2\">Event 2</td><td colspan=\"2\">Event 3</td><td colspan=\"2\">Event 4</td></tr><tr align=\"center\" bgcolor=\"#66CCFF\"><td>Name</td><td>Signal</td><td>Name</td><td>Signal</td><td>Name</td><td>Signal</td><td>Name</td><td>Signal</td></tr><tr align=\"center\">";
    for (var i = 0; i < prevPosMtxTemp.length; i++) {
        for (var j = 0; j < 14; j++) {
            currTableMtx3 += "<td bgcolor=\"#" + prevEventColors[i] + "\">" + prevPosMtx[i][j] + "</td>";
        }
        for (var j = 14; j < 16; j++) {
            currTableMtx3 += "<td bgcolor=\"#" + prevEventColors[i] + "\"><b>" + prevPosMtx[i][j] + "</b></td>";
        }
        currTableMtx3 += "</tr>";
    }
    currTableMtx3 += "</table>";

    var currTableMtx5 = "<table class=\"currData\"><tr align=\"center\"  bgcolor=\"#66CCFF\"><td rowspan=\"3\">Date</td><td colspan=\"8\">Events</td><td rowspan=\"3\">Most Significant Event</td><td rowspan=\"3\">M.S. Event Signal</td><td rowspan=\"3\">M.S. Event Leverage</td></tr><tr align=\"center\" bgcolor=\"#66CCFF\"><td colspan=\"2\">Event 1</td><td colspan=\"2\">Event 2</td><td colspan=\"2\">Event 3</td><td colspan=\"2\">Event 4</td></tr><tr align=\"center\" bgcolor=\"#66CCFF\"><td>Name</td><td>Signal</td><td>Name</td><td>Signal</td><td>Name</td><td>Signal</td><td>Name</td><td>Signal</td></tr><tr align=\"center\">";
    for (var i = 0; i < nextPosMtxTemp.length; i++) {
        for (var j = 0; j < 12; j++) {
            currTableMtx5 += "<td bgcolor=\"#" + nextEventColors[i] + "\">" + nextPosMtx[i][j] + "</td>";
        }
        currTableMtx5 += "</tr>";
    }
    currTableMtx5 += "</table>";

    var currTableMtx7 = "<table class=\"currData2\"><tr><td bgcolor=\"#32CD32\">FOMC Bullish Day</td><td bgcolor=\"#7CFC00\">Holiday Bullish Day</td><td bgcolor=\"#00FA9A\">Other Bullish Event Day</td></tr><tr><td bgcolor=\"#c24f4f\">FOMC Bearish Day</td><td bgcolor=\"#d46a6a\">Holiday Bearish Day</td><td bgcolor=\"#ed8c8c\">Other Bearish Event Day</td></tr><tr><td bgcolor=\"#C0C0C0\">Non-Playable Other Event Day</td><td bgcolor=\"#00FFFF\">STCI Bullish Day</td><td bgcolor=\"#FFFACD\">STCI Neutral Day</td></tr></table > ";

    //"Sending" data to HTML file.
    var currTableMtx2 = document.getElementById("idCurrTableMtx");
    currTableMtx2.innerHTML = currTableMtx;
    var currTableMtx4 = document.getElementById("idCurrTableMtx3");
    currTableMtx4.innerHTML = currTableMtx3;
    var currTableMtx6 = document.getElementById("idCurrTableMtx5");
    currTableMtx6.innerHTML = currTableMtx5;
    var currTableMtx8 = document.getElementById("idCurrTableMtx7");
    currTableMtx8.innerHTML = currTableMtx7;

    //Declaring data sets to charts.

    var nCurrData = parseInt(data.chartLength) + 1;

    var xTicksH = new Array(nCurrData);
    for (var i = 0; i < nCurrData; i++) {
        var xTicksHRows = new Array(2);
        xTicksHRows[0] = i;
        xTicksHRows[1] = assChartMtx[i][0];
        xTicksH[i] = xTicksHRows;
    }

    var noAssets = assetNames2Array.length - 1;
    var listH = [];
    for (var j = 0; j < noAssets; j++) {
        var assChartPerc1 = new Array(nCurrData);
        for (var i = 0; i < nCurrData; i++) {
            var assChartPerc1Rows = new Array(2);
            assChartPerc1Rows[0] = i;
            assChartPerc1Rows[1] = parseFloat(assChartMtx[i][j + 1]);
            assChartPerc1[i] = assChartPerc1Rows;
        }
        listH.push({ label: assetNames2Array[j], data: assChartPerc1, points: { show: true }, lines: { show: true } });
    }



    var datasets1 = listH;



    var monthList = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    var currDataVixArray = data.currDataVixVec.split(",");
    var currDataDaysVixArray = data.currDataDaysVixVec.split(",");
    var prevDataVixArray = data.prevDataVixVec.split(",");
    var currDataDiffVixArray = data.currDataDiffVixVec.split(",");
    var currDataPercChVixArray = data.currDataPercChVixVec.split(",");
    var spotVixArray = data.spotVixVec.split(",");


    //Creating the HTML code of current table.
    var currTableMtx9 = "<table class=\"currData\"><tr align=\"center\"><td>Future Prices</td><td>F1</td><td>F2</td><td>F3</td><td>F4</td><td>F5</td><td>F6</td><td>F7</td><td>F8</td></tr><tr align=\"center\"><td align=\"left\">Current</td>";
    for (var i = 0; i < 8; i++) {
        if (currDataVixArray[i] == 0) {
            currTableMtx9 += "<td>" + "---" + "</td>";
        } else {
            currTableMtx9 += "<td>" + currDataVixArray[i] + "</td>";
        }
    }

    currTableMtx9 += "</tr><tr align=\"center\"><td align=\"left\">Previous Close</td>";
    for (var i = 0; i < 8; i++) {
        if (currDataVixArray[i] == 0) {
            currTableMtx9 += "<td>" + "---" + "</td>";
        } else {
            currTableMtx9 += "<td>" + prevDataVixArray[i] + "</td>";
        }
    }
    currTableMtx9 += "</tr><tr align=\"center\"><td align=\"left\">Daily Abs. Change</td>";
    for (var i = 0; i < 8; i++) {
        if (currDataVixArray[i] == 0) {
            currTableMtx9 += "<td>" + "---" + "</td>";
        } else {
            currTableMtx9 += "<td>" + currDataDiffVixArray[i] + "</td>";
        }
    }
    currTableMtx9 += "</tr><tr align=\"center\"><td align=\"left\">Daily % Change</td>";
    for (var i = 0; i < 8; i++) {
        if (currDataVixArray[i] == 0) {
            currTableMtx9 += "<td>" + "---" + "</td>";
        } else {
            currTableMtx9 += "<td>" + (currDataPercChVixArray[i] * 100).toFixed(2) + "%</td>";
        }
    }
    currTableMtx9 += "</tr><tr align=\"center\"><td align=\"left\">Cal. Days to Expiration</td>";
    for (var i = 0; i < 8; i++) {
        if (currDataVixArray[i] == 0) {
            currTableMtx9 += "<td>" + "---" + "</td>";
        }
        else {
            currTableMtx9 += "<td>" + currDataDaysVixArray[i] + "</td>";
        }
    }
    currTableMtx9 += "</tr></table>";

    var currTableMtx11 = "<table class=\"currData\"><tr align=\"center\"><td>Contango</td><td>F2-F1</td><td>F3-F2</td><td>F4-F3</td><td>F5-F4</td><td>F6-F5</td><td>F7-F6</td><td>F8-F7</td><td>F7-F4</td><td>(F7-F4)/3</td></tr><tr align=\"center\"><td align=\"left\">Monthly Contango %</td><td><strong>" + (currDataVixArray[i] * 100).toFixed(2) + "%</strong></td>";
    for (var i = 20; i < 27; i++) {
        if (currDataVixArray[i] == 0) {
            currTableMtx11 += "<td>" + 0 + "</td>";
        } else {
            currTableMtx11 += "<td>" + (currDataVixArray[i] * 100).toFixed(2) + "%</td>";
        }
    }
    currTableMtx11 += "<td><strong>" + (currDataVixArray[27] * 100).toFixed(2) + "%</strong></td>";
    currTableMtx11 += "</tr><tr align=\"center\"><td align=\"left\">Difference</td>";
    for (var i = 10; i < 19; i++) {
        if (currDataVixArray[i] == 0) {
            currTableMtx11 += "<td>" + "0%" + "</td>";
        }
        else {
            currTableMtx11 += "<td>" + (currDataVixArray[i] * 100 / 100).toFixed(2) + "</td>";
        }
    }
    currTableMtx11 += "</tr></table>";

    //"Sending" data to HTML file.
    var currTableMtx10 = document.getElementById("idCurrTableMtx9");
    currTableMtx10.innerHTML = currTableMtx9;
    var currTableMtx12 = document.getElementById("idCurrTableMtx11");
    currTableMtx12.innerHTML = currTableMtx11;

    var nCurrDataVix = 7;
    var currDataVixPrices = new Array(nCurrDataVix);
    for (var i = 0; i < nCurrDataVix; i++) {
        var currDataVixPricesRows = new Array(2);
        currDataVixPricesRows[0] = currDataDaysVixArray[i];
        currDataVixPricesRows[1] = currDataVixArray[i];
        currDataVixPrices[i] = currDataVixPricesRows;
    }

    var prevDataVixPrices = new Array(nCurrDataVix);
    for (var i = 0; i < nCurrDataVix; i++) {
        var prevDataVixPricesRows = new Array(2);
        prevDataVixPricesRows[0] = currDataDaysVixArray[i];
        prevDataVixPricesRows[1] = prevDataVixArray[i];
        prevDataVixPrices[i] = prevDataVixPricesRows;
    }

    var spotVixValues = new Array(nCurrDataVix);
    for (var i = 0; i < nCurrDataVix; i++) {
        var spotVixValuesRows = new Array(2);
        spotVixValuesRows[0] = currDataDaysVixArray[i];
        spotVixValuesRows[1] = spotVixArray[i];
        spotVixValues[i] = spotVixValuesRows;
    }


    //Declaring data sets to charts.

    var datasets2 = {
        "current": {
            label: "Current",
            data: currDataVixPrices,
            points: { show: true },
            lines: { show: true }
        },
        "previous": {
            label: "Last close",
            data: prevDataVixPrices,
            points: { show: true },
            lines: { show: true }
        },
        "spot": {
            label: "Spot VIX",
            data: spotVixValues,
            //points: { show: false },
            lines: { show: true, lineWidth: 1 }
            //dashes: { show: true, lineWidth: 5 }
        }
    };





    flotPlotMyData1(datasets1, nCurrData, xTicksH, noAssets);
    flotPlotMyData2(datasets2);




}
// Creating charts.
function flotPlotMyData1(datasets1, nCurrData, xTicksH, noAssets) {

    var dataB = [];
    $.each(datasets1, function (key) {
        dataB.push(datasets1[key]);
    });

    $.plot("#placeholder1", dataB,
        {
            yaxis: {
                axisLabel: "Percentage Change",
                tickFormatter: function (v, axis) {
                    return v.toFixed(0) + "%";
                }

            },
            xaxis: {
                //tickDecimals: 0,
                min: 0,
                //max: nCurrData-1,
                ticks: xTicksH,
                axisLabel: "Day"
            },
            legend: {
                position: "nw",
                noColumns: noAssets,
                backgroundColor: "#F4F6F6"
            },
            grid: {
                backgroundColor: "#F4F6F6",
                hoverable: true
            },
            tooltip: {
                show: true,
                content: function (label, x, y) {
                    var xVals = [];
                    for (var i = 0; i < nCurrData; i++) {
                        xVals[i] = dataB[0].data[i][0];
                    };
                    var indi = xVals.indexOf(x);

                    var text = "<b>" + label + "<br/></b><i>" + indi + "-Day Percentage Changes on " + xTicksH[indi][1] + "<br/></i>";
                    dataB.forEach(function (series) {
                        text += series.label + ' : ' + series.data[indi][1] + "%<br/>";
                    });

                    return text;
                }
            }

        });

}

function flotPlotMyData2(datasets2) {
    //-- hard-code color indices to prevent them from shifting as
    //-- countries are turned on/off

    var i = 0;
    $.each(datasets2, function (key, val) {
        val.color = i;
        ++i;
    });

    // insert checkboxes
    $("#choices1").html("");
    var choiceContainer = $("#choices1");
    $.each(datasets2, function (key, val) {
        if (val.color < 5) {
            choiceContainer.append("<br/><input type='checkbox' name='" + key +
                "' checked='checked' id='id" + key + "'></input>" +
                "<label for='id" + key + "'>"
                + val.label + "</label>");
        }
        else {
            choiceContainer.append("<br/><input type='checkbox' name='" + key +
                "' id='id" + key + "'></input>" +
                "<label for='id" + key + "'>"
                + val.label + "</label>");
        }
    });

    choiceContainer.find("input").click(plotAccordingToChoices);

    function plotAccordingToChoices() {

        var dataB = [];

        choiceContainer.find("input:checked").each(function () {
            var key = $(this).attr("name");
            if (key && datasets2[key]) {
                dataB.push(datasets2[key]);
            }
        });


        if (dataB.length > 0) {
            $.plot("#placeholder2", dataB, {
                yaxis: {
                    axisLabel: "Futures Price (USD)",
                    //tickFormatter: function (v, axis) {
                    //    return "$" + v.toFixed(1);
                    //}

                },
                xaxis: {
                    tickDecimals: 0,
                    min: 0,
                    max: 225,
                    axisLabel: "Number of Days till Settlement"
                },
                legend: {
                    position: "nw",
                    backgroundColor: "#F4F6F6"
                },
                colors: ["#0022FF", "#148F77", "#CB4335"],
                grid: {
                    backgroundColor: "#F4F6F6",
                    hoverable: true
                },
                tooltip: {
                    show: true,
                    //},
                    //tooltipOpts: {
                    //content: function (label, xval, yval) {
                    //    var content = "%s %x " + yval;
                    //    return content;
                    //}
                    content: function (label, x, y) {
                        var xVals = [];
                        for (var i = 0; i < 7; i++) {
                            xVals[i] = dataB[0].data[i][0];
                        };
                        var indi = xVals.indexOf(x);
                        var text = "<i>Number of days till expiration: " + dataB[0].data[indi][0] + "<br/></i>";
                        dataB.forEach(function (series) {
                            // series_label : value
                            text += series.label + ' : ' + series.data[indi][1] + "<br/>";
                        });

                        return text;
                    }
                }

            });
        }
    }

    plotAccordingToChoices();

}