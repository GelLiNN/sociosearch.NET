﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Sociosearch.NET.Models;
using TinyCsvParser;
using TinyCsvParser.Mapping;
using TinyCsvParser.Model;
using TinyCsvParser.TypeConverter;

namespace Sociosearch.NET.Middleware
{
    public static class FINRA
    {
        public static readonly DateTime FirstDate = new DateTime(2018, 11, 5);
        public static string BaseUrl = @"http://regsho.finra.org";

        private static readonly HttpClient Client = new HttpClient();

        public static ShortInterestResult GetShortInterest(string symbol, int daysToCalculate)
        {
            decimal compositeScore = 0;

            List<FinraRecord> shortRecords = GetShortVolume(symbol, daysToCalculate);

            int daysCalulated = 0;
            int numberOfResults = 0;
            HashSet<string> dates = new HashSet<string>();
            Stack<decimal> shortInterestYList = new Stack<decimal>();

            decimal shortInterestToday = 0;
            decimal totalVolume = 0;
            decimal totalVolumeShort = 0;
            foreach (FinraRecord shortRecord in shortRecords)
            {
                if (daysCalulated < daysToCalculate)
                {
                    decimal shortVolume = shortRecord.ShortVolume + shortRecord.ShortExemptVolume;
                    decimal shortInterest = (shortVolume / shortRecord.TotalVolume) * 100;

                    if (numberOfResults == 0)
                        shortInterestToday = shortInterest;

                    shortInterestYList.Push(shortInterest);
                    totalVolume += shortRecord.TotalVolume;
                    totalVolumeShort += shortVolume;
                    numberOfResults++;

                    string shortDate = shortRecord.Date.ToString("yyyy-MM-dd");
                    if (!dates.Contains(shortDate))
                    {
                        dates.Add(shortDate);
                        daysCalulated++;
                    }
                }
                else
                    break;
            }

            List<decimal> shortXList = new List<decimal>();
            for (int i = 1; i <= numberOfResults; i++)
                shortXList.Add(i);

            List<decimal> shortYList = shortInterestYList.ToList();
            decimal shortSlope = TD.GetSlope(shortXList, shortYList);
            decimal shortSlopeMultiplier = TD.GetSlopeMultiplier(shortSlope);
            decimal shortInterestAverage = (totalVolumeShort / totalVolume) * 100;

            //Add these bonuses to account for normal short interest fluctuations
            bool slightlyBearish = (0.0M <= shortSlope && shortSlope <= 0.5M);
            bool moderatelyBearish = (0.5M <= shortSlope && shortSlope <= 1.0M);

            //calculate composite score based on the following values and weighted multipliers
            compositeScore += 100 - shortInterestAverage; //get score as 100 - short interest
            compositeScore += (shortSlope < 0) ? (shortSlope * shortSlopeMultiplier) + 20 : -5;
            compositeScore += (shortSlope > 0 && slightlyBearish) ? 15 : 0;
            compositeScore += (shortSlope > 0 && moderatelyBearish) ? 10 : 0;

            //Return ShortInterestResult
            return new ShortInterestResult
            {
                TotalVolume = totalVolume,
                TotalVolumeShort = totalVolumeShort,
                ShortInterestPercentToday = shortInterestToday,
                ShortInterestPercentAverage = shortInterestAverage,
                ShortInterestSlope = shortSlope,
                ShortInterestCompositeScore = compositeScore
            };
        }

        public static List<FinraRecord> GetShortVolume(string symbol, int days)
        {
            List<FinraRecord> shortRecords = new List<FinraRecord>();

            //Get last 14 trading days for this symbol using TD
            //This way the FINRA short interest module completely relies on TD for dates
            string ochlResponse = TD.CompleteTwelveDataRequest("time_series", symbol).Result;
            JObject data = JObject.Parse(ochlResponse);
            JArray resultSet = (JArray)data.GetValue("values");

            for (int i = 0; i < days; i++)
            {
                var ochlResult = resultSet[i];
                string ochlResultDate = ochlResult.Value<string>("datetime");

                DateTime date = DateTime.Parse(ochlResultDate);
                //DateTime date = DateTime.Now.AddDays(-1 * i);

                if (DateTime.Compare(date, FirstDate) >= 0)
                {
                    List<FinraRecord> allRecords = GetAllShortVolume(date).Result;
                    FinraRecord curDayRecord = allRecords.Where(x => x.Symbol == symbol).FirstOrDefault();

                    if (curDayRecord != null)
                        shortRecords.Add(curDayRecord);
                }
            }
            return shortRecords;
        }

        public static async Task<List<FinraRecord>> GetAllShortVolume(DateTime date)
        {
            // example URL: http://regsho.finra.org/CNMSshvol20181105.txt
            string dateString = date.ToString("yyyyMMdd");
            string fileName = $"CNMSshvol{dateString}.txt";
            string requestUrl = $"{BaseUrl}/{fileName}";

            var response = await Client.GetStringAsync(requestUrl);
            var finraResponse = FinraResponseParser.ParseResponse(response);

            return await Task.FromResult(finraResponse);
        }

        public class FinraResponseParser
        {
            public static int count = 0;
            public static List<FinraRecord> ParseResponse(string finraResponse)
            {
                CsvParserOptions csvParserOptions = new CsvParserOptions(true, '|');
                CsvPersonMapping mapping = new CsvPersonMapping();
                CsvParser<FinraRecord> csvParser = new CsvParser<FinraRecord>(csvParserOptions, mapping);

                CsvReaderOptions csvReaderOptions = new CsvReaderOptions(new string[] { "\r\n" });
                var result = csvParser.ReadFromFinraString(csvReaderOptions, finraResponse, 2);

                var a = result.ToList();
                return result.Select(r => r.Result).ToList();
            }

            private class CsvPersonMapping : CsvMapping<FinraRecord>
            {
                private static readonly DtConverter dtConverter = new DtConverter();
                public CsvPersonMapping()
                    : base()
                {
                    MapProperty(0, x => x.Date, dtConverter);
                    MapProperty(1, x => x.Symbol);
                    MapProperty(2, x => x.ShortVolume);
                    MapProperty(3, x => x.ShortExemptVolume);
                    MapProperty(4, x => x.TotalVolume);
                    MapProperty(5, x => x.Market);
                }
            }

            private class DtConverter : ITypeConverter<DateTime>
            {
                public Type TargetType => throw new NotImplementedException();
                private static readonly string validFormat = "yyyyMMdd";
                private static CultureInfo cultureInfo = new CultureInfo("en-US");

                public bool TryConvert(string value, out DateTime result)
                {
                    result = DateTime.ParseExact(value, validFormat, cultureInfo);
                    FinraResponseParser.count++;
                    return true;
                }
            }
        }
    }

    public static class CsvParserExtensions
    {
        public static ParallelQuery<CsvMappingResult<TEntity>> ReadFromFinraString<TEntity>(this CsvParser<TEntity> csvParser, CsvReaderOptions csvReaderOptions, string csvData, int skipLast)
        {
            var lines = csvData
                .Split(csvReaderOptions.NewLine, StringSplitOptions.None)
                .SkipLast(skipLast)
                .Select((line, index) => new Row(index, line));

            return csvParser.Parse(lines);
        }
    }
}