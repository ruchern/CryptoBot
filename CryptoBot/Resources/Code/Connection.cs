﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SqlTypes;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using Bittrex.Api.Client._2.BusinessLogic;
using BusinessLayer;
using BusinessLayer.Models;
using CryptoBot.Properties;
using BusinessLayer.BusinessLogic;
using System.Xml.Serialization;
using System.IO;


namespace CryptoBot.Code.Connection
{
    public class Connection
    {
        private static string secureKey => Settings.Default.APISecureKey;
        private static string secureSecret => Settings.Default.APISecureSecret;
        public static BittrexClient bittrexClient => new BittrexClient(Settings.Default.APIBaseAddress, StringCipher.Decrypt(secureKey), StringCipher.Decrypt(secureSecret));
        public static List<OpenOrder> OpenOrders { get; set; } = new List<OpenOrder>();
        public static List<AccountBalance> AccountBalances { get; set; } = new List<AccountBalance>();

        private static List<string> _notableCurrenciesList;
        public static List<string> NotableCurrenciesList
        {
            get { return _notableCurrenciesList ?? (_notableCurrenciesList = new List<string>()); }
            set { _notableCurrenciesList = value; }
        }

        public static MarketSummary BitcoinUsdt { get; set; }

        public static ObservableCollection<MarketSummary> MarketSummaries { get; set; } = new ObservableCollection<MarketSummary>();

        public static List<MarketSummary> FullMarketList { get; set; } = new List<MarketSummary>();

        public static List<BusinessLayer.Models.Market> Markets { get; set; } = new List<BusinessLayer.Models.Market>();

        public static async Task Populate()
        {
            //balances
            var balances = await bittrexClient.GetBalances();
            if (balances.Success)
            {
                AccountBalances = balances.Result.Where(d => d.Balance > 0).ToList();
            }
            else
            {
                throw new Exception(balances.Message);
            }
            //orders
            var orders = await bittrexClient.GetOpenOrders();
            if (orders.Success)
            {
                OpenOrders = orders.Result.OrderBy(d => d.OrderType).ThenBy(n => n.Exchange).ThenByDescending(q => q.Quantity).ToList();
            }
            else
            {
                throw new Exception(orders.Message);
            }
            PopulateNotableCurrencies();

            //Market summaries
            var summary = await bittrexClient.GetMarketSummaries();
            if (summary.Success)
            {
                FullMarketList = summary.Result.ToList();
                var summaries = summary.Result.Where(d => NotableCurrenciesList.Any(e => string.Equals(e, d.MarketName, StringComparison.CurrentCultureIgnoreCase)));
                MarketSummaries = new ObservableCollection<MarketSummary>(summaries.OrderBy(d => d.MarketName == "USDT-BTC" ? 0 : 1).ToList());
            }
            else
            {
                throw new Exception(summary.Message);
            }
            var btcvalue = await bittrexClient.GetMarketSummary("USDT-BTC");
            if (btcvalue.Success)
            {
                BitcoinUsdt = btcvalue.Result;
            }

            var markets = await bittrexClient.GetMarkets();
            if (markets.Success)
            {
                Markets = markets.Result.ToList();
            }
            else
            {
                throw new Exception(summary.Message);
            }
        }

        public static decimal? GetMinimumBuyForCurrency(string currency)
        {
            var market = Markets.FirstOrDefault(d => d.MarketCurrency.ToString().ToUpper().Trim() == currency.ToUpper().Trim());
            return market?.MinTradeSize;
        }

        private static void PopulateNotableCurrencies()
        {
            List<string> currencyList = new List<string> { "USDT-BTC" };
            //Notable Currencies
            foreach (var balance in AccountBalances)
            {
                if (!currencyList.Contains(balance.Currency))
                {
                    currencyList.Add("BTC-" + balance.Currency);
                }
            }
            foreach (var order in OpenOrders)
            {
                var currencies = order.Exchange.Split('-');
                foreach (var c in currencies)
                {
                    if (!currencyList.Contains(c))
                    {
                        currencyList.Add("BTC-" + c);
                    }
                }
            }
            NotableCurrenciesList = currencyList;
        }

        public static int[] ProcessSchedules()
        {
            int[] count = new int[2] { 0, 0 };
            //Process sells first
            foreach (var order in ScheduleHandler.MasterSchedule.Orders.Where(d => d.OrderType == OrderType.Sell && d.Sent == null))
            {
                try
                {
                    var bidPrice = FullMarketList.FirstOrDefault(d => d.MarketName == order.MarketName)?.Bid;
                    if (bidPrice != null && bidPrice > order.Bid)
                    {
                        order.ActualBid = (decimal)bidPrice;
                    }
                    else
                    {
                        order.ActualBid = order.Bid;
                    }

                    var task = new Task<Task>(async () =>
                    {
                        await SendSellOrder(order.MarketName, order);
                    });
                    task.Start();
                    task.Wait();
                    task.Result.Wait();
                    order.Sent = DateTime.Now;

                    order.LastOutcome = "Success";
                    count[0] += 1;
                }
                catch (Exception ex)
                {
                    order.LastOutcome = ex.InnerException.Message;
                }
            }

            //Process buys next
            foreach (var order in ScheduleHandler.MasterSchedule.Orders.Where(d => d.OrderType == OrderType.Buy && d.Sent == null))
            {
                try
                {
                    var askingPrice = FullMarketList.FirstOrDefault(d => d.MarketName == order.MarketName)?.Bid;
                    if (askingPrice != null && askingPrice < order.Bid)
                    {
                        order.ActualBid = (decimal)askingPrice;
                    }
                    else
                    {
                        order.ActualBid = order.Bid;
                    }

                    var task = new Task<Task>(async () =>
                {
                    await SendBuyOrder(order.MarketName, order);
                });
                    task.Start();
                    task.Wait();
                    task.Result.Wait();
                    order.Sent = DateTime.Now;
                    order.LastOutcome = "Success";
                    count[1] += 1;

                }
                catch (Exception ex)
                {
                    order.LastOutcome = ex.InnerException.Message;
                }
            }
            ScheduleHandler.SaveMasterSchedule();
            return count;
        }


        public static async Task SendBuyOrder(string marketName, Order order)
        {
            var buy = await bittrexClient.BuyLimit(marketName, order.Qty, order.ActualBid);
            if (!buy.Success)
            {
                throw new Exception(buy.Message, null);
            }
        }

        public static async Task SendSellOrder(string marketName, Order order)
        {

            var sell = await bittrexClient.SellLimit(marketName, order.Qty, order.ActualBid);
            if (!sell.Success)
            {
                throw new Exception(sell.Message);
            }
        }
    }
}
