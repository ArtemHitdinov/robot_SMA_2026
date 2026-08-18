/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;

/* Description
Screener robot for osEngine.

Long: fast SMA crosses above slow SMA (golden cross).
Short: fast SMA crosses below slow SMA (death cross).
Exit long: death cross, trailing stop, stop/take on open.
Exit short: golden cross, trailing stop, stop/take on open.
AI curosr
MOEX non-trade periods: weekdays 10:00-18:00, no weekends.
*/

namespace OsEngine.Robots.MyBots
{
    [Bot("MySmaCrossScreener")]
    public class MySmaCrossScreener : BotPanel
    {
        private BotTabScreener _screenerTab;

        private StrategyParameterString _regime;
        private StrategyParameterInt _maxPositions;
        private StrategyParameterInt _slippage;

        private StrategyParameterString _volumeType;
        private StrategyParameterDecimal _volume;
        private StrategyParameterString _tradeAssetInPortfolio;

        private StrategyParameterInt _fastSmaLength;
        private StrategyParameterInt _slowSmaLength;

        private StrategyParameterDecimal _stopLossPercent;
        private StrategyParameterDecimal _takeProfitPercent;
        private StrategyParameterDecimal _trailStopPercent;

        private NonTradePeriods _tradePeriodsSettings;
        private StrategyParameterButton _tradePeriodsShowDialogButton;

        public MySmaCrossScreener(string name, StartProgram startProgram) : base(name, startProgram)
        {
            _tradePeriodsSettings = new NonTradePeriods(name);

            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod1Start = new TimeOfDay() { Hour = 0, Minute = 0 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod1End = new TimeOfDay() { Hour = 10, Minute = 5 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod1OnOff = true;

            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod3Start = new TimeOfDay() { Hour = 18, Minute = 1 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod3End = new TimeOfDay() { Hour = 23, Minute = 58 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod3OnOff = true;

            _tradePeriodsSettings.TradeInSunday = false;
            _tradePeriodsSettings.TradeInSaturday = false;
            _tradePeriodsSettings.Load();

            TabCreate(BotTabType.Screener);
            _screenerTab = TabsScreener[0];

            _screenerTab.CandleFinishedEvent += _screenerTab_CandleFinishedEvent;
            _screenerTab.PositionOpeningSuccesEvent += _screenerTab_PositionOpeningSuccesEvent;

            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyLong", "OnlyShort" });
            _maxPositions = CreateParameter("Max positions", 5, 1, 50, 1);
            _slippage = CreateParameter("Slippage", 0, 0, 20, 1);

            _volumeType = CreateParameter("Volume type", "Deposit percent", new[] { "Contracts", "Contract currency", "Deposit percent" });
            _volume = CreateParameter("Volume", 10, 1.0m, 50, 4);
            _tradeAssetInPortfolio = CreateParameter("Asset in portfolio", "Prime");

            _fastSmaLength = CreateParameter("Fast Sma length", 20, 5, 200, 1);
            _slowSmaLength = CreateParameter("Slow Sma length", 50, 10, 300, 1);

            _stopLossPercent = CreateParameter("Stop loss %", 1.0m, 0, 10, 0.1m);
            _takeProfitPercent = CreateParameter("Take profit %", 2.0m, 0, 20, 0.1m);
            _trailStopPercent = CreateParameter("Trail stop %", 0.7m, 0, 10, 0.1m);

            _tradePeriodsShowDialogButton = CreateParameterButton("Non trade periods");
            _tradePeriodsShowDialogButton.UserClickOnButtonEvent += _tradePeriodsShowDialogButton_UserClickOnButtonEvent;

            _screenerTab.CreateCandleIndicator(1, "Sma",
                new List<string>() { _fastSmaLength.ValueInt.ToString(), "Close" }, "Prime");
            _screenerTab.CreateCandleIndicator(2, "Sma",
                new List<string>() { _slowSmaLength.ValueInt.ToString(), "Close" }, "Prime");

            ParametrsChangeByUser += MySmaCrossScreener_ParametrsChangeByUser;
            DeleteEvent += MySmaCrossScreener_DeleteEvent;
        }

        private void MySmaCrossScreener_DeleteEvent()
        {
            try
            {
                _tradePeriodsSettings.Delete();
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void _tradePeriodsShowDialogButton_UserClickOnButtonEvent()
        {
            _tradePeriodsSettings.ShowDialog();
        }

        private void MySmaCrossScreener_ParametrsChangeByUser()
        {
            _screenerTab._indicators[0].Parameters =
                new List<string>() { _fastSmaLength.ValueInt.ToString(), "Close" };
            _screenerTab._indicators[1].Parameters =
                new List<string>() { _slowSmaLength.ValueInt.ToString(), "Close" };

            _screenerTab.UpdateIndicatorsParameters();
        }

        private void _screenerTab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            try
            {
                if (_regime.ValueString == "Off")
                {
                    return;
                }

                if (StartProgram == StartProgram.IsOsOptimizer)
                {
                    return;
                }

                int minCandles = Math.Max(_fastSmaLength.ValueInt, _slowSmaLength.ValueInt) + 5;

                if (candles.Count < minCandles)
                {
                    return;
                }

                if (_tradePeriodsSettings.CanTradeThisTime(candles[^1].TimeStart) == false)
                {
                    return;
                }

                Aindicator fastSma = (Aindicator)tab.Indicators[0];
                Aindicator slowSma = (Aindicator)tab.Indicators[1];

                int lastIndex = candles.Count - 1;
                int prevIndex = lastIndex - 1;

                decimal fastLast = fastSma.DataSeries[0].Values[lastIndex];
                decimal slowLast = slowSma.DataSeries[0].Values[lastIndex];
                decimal fastPrev = fastSma.DataSeries[0].Values[prevIndex];
                decimal slowPrev = slowSma.DataSeries[0].Values[prevIndex];

                if (fastLast == 0 || slowLast == 0 || fastPrev == 0 || slowPrev == 0)
                {
                    return;
                }

                bool goldenCross = fastPrev <= slowPrev && fastLast > slowLast;
                bool deathCross = fastPrev >= slowPrev && fastLast < slowLast;

                List<Position> positions = tab.PositionsOpenAll;

                if (positions.Count == 0)
                {
                    if (_screenerTab.PositionsOpenAll.Count >= _maxPositions.ValueInt)
                    {
                        return;
                    }

                    if (goldenCross && _regime.ValueString != "OnlyShort")
                    {
                        tab.BuyAtMarket(GetVolume(tab));
                    }
                    else if (deathCross && _regime.ValueString != "OnlyLong")
                    {
                        tab.SellAtMarket(GetVolume(tab));
                    }
                }
                else
                {
                    Position position = positions[0];

                    if (position.State != PositionStateType.Open)
                    {
                        return;
                    }

                    if (position.Direction == Side.Buy && deathCross)
                    {
                        tab.CloseAtMarket(position, position.OpenVolume);
                    }
                    else if (position.Direction == Side.Sell && goldenCross)
                    {
                        tab.CloseAtMarket(position, position.OpenVolume);
                    }
                    else if (_trailStopPercent.ValueDecimal > 0)
                    {
                        UpdateTrailingStop(candles, tab, position);
                    }
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void UpdateTrailingStop(List<Candle> candles, BotTabSimple tab, Position position)
        {
            if (position.State != PositionStateType.Open)
            {
                return;
            }

            decimal referencePrice;

            if (position.Direction == Side.Buy)
            {
                referencePrice = candles[candles.Count - 1].Low;
                decimal priceActivation = referencePrice - referencePrice * _trailStopPercent.ValueDecimal / 100m;
                decimal priceOrder = priceActivation - tab.Security.PriceStep * _slippage.ValueInt;
                tab.CloseAtTrailingStop(position, priceActivation, priceOrder);
            }
            else
            {
                referencePrice = candles[candles.Count - 1].High;
                decimal priceActivation = referencePrice + referencePrice * _trailStopPercent.ValueDecimal / 100m;
                decimal priceOrder = priceActivation + tab.Security.PriceStep * _slippage.ValueInt;
                tab.CloseAtTrailingStop(position, priceActivation, priceOrder);
            }
        }

        private void _screenerTab_PositionOpeningSuccesEvent(Position position, BotTabSimple tab)
        {
            try
            {
                if (position.State != PositionStateType.Open)
                {
                    return;
                }

                if (_stopLossPercent.ValueDecimal > 0 && _trailStopPercent.ValueDecimal <= 0)
                {
                    decimal stopPrice;

                    if (position.Direction == Side.Buy)
                    {
                        stopPrice = position.EntryPrice - position.EntryPrice * _stopLossPercent.ValueDecimal / 100m;
                        tab.CloseAtStopMarket(position, stopPrice, "StopLoss");
                    }
                    else
                    {
                        stopPrice = position.EntryPrice + position.EntryPrice * _stopLossPercent.ValueDecimal / 100m;
                        tab.CloseAtStopMarket(position, stopPrice, "StopLoss");
                    }
                }

                if (_takeProfitPercent.ValueDecimal > 0)
                {
                    decimal profitPrice;

                    if (position.Direction == Side.Buy)
                    {
                        profitPrice = position.EntryPrice + position.EntryPrice * _takeProfitPercent.ValueDecimal / 100m;
                        tab.CloseAtProfitMarket(position, profitPrice, "TakeProfit");
                    }
                    else
                    {
                        profitPrice = position.EntryPrice - position.EntryPrice * _takeProfitPercent.ValueDecimal / 100m;
                        tab.CloseAtProfitMarket(position, profitPrice, "TakeProfit");
                    }
                }

                if (_trailStopPercent.ValueDecimal > 0)
                {
                    List<Candle> candles = tab.CandlesFinishedOnly;

                    if (candles != null && candles.Count > 0)
                    {
                        UpdateTrailingStop(candles, tab, position);
                    }
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private decimal GetVolume(BotTabSimple tab)
        {
            decimal volume = 0;

            if (_volumeType.ValueString == "Contracts")
            {
                volume = _volume.ValueDecimal;
            }
            else if (_volumeType.ValueString == "Contract currency")
            {
                decimal contractPrice = tab.PriceBestAsk;
                volume = _volume.ValueDecimal / contractPrice;

                if (StartProgram == StartProgram.IsOsTrader)
                {
                    IServerPermission serverPermission = ServerMaster.GetServerPermission(tab.Connector.ServerType);

                    if (serverPermission != null &&
                        serverPermission.IsUseLotToCalculateProfit &&
                        tab.Security.Lot != 0 &&
                        tab.Security.Lot > 1)
                    {
                        volume = _volume.ValueDecimal / (contractPrice * tab.Security.Lot);
                    }

                    volume = Math.Round(volume, tab.Security.DecimalsVolume);
                }
                else
                {
                    volume = Math.Round(volume, 6);
                }
            }
            else if (_volumeType.ValueString == "Deposit percent")
            {
                Portfolio myPortfolio = tab.Portfolio;

                if (myPortfolio == null)
                {
                    return 0;
                }

                decimal portfolioPrimeAsset = 0;

                if (_tradeAssetInPortfolio.ValueString == "Prime")
                {
                    portfolioPrimeAsset = myPortfolio.ValueCurrent;
                }
                else
                {
                    List<PositionOnBoard> positionOnBoard = myPortfolio.GetPositionOnBoard();

                    if (positionOnBoard == null)
                    {
                        return 0;
                    }

                    for (int i = 0; i < positionOnBoard.Count; i++)
                    {
                        if (positionOnBoard[i].SecurityNameCode == _tradeAssetInPortfolio.ValueString)
                        {
                            portfolioPrimeAsset = positionOnBoard[i].ValueCurrent;
                            break;
                        }
                    }
                }

                if (portfolioPrimeAsset == 0)
                {
                    SendNewLogMessage("Can`t found portfolio " + _tradeAssetInPortfolio.ValueString, LogMessageType.Error);
                    return 0;
                }

                decimal moneyOnPosition = portfolioPrimeAsset * (_volume.ValueDecimal / 100);

                decimal qty = moneyOnPosition / tab.PriceBestAsk / tab.Security.Lot;

                if (tab.StartProgram == StartProgram.IsOsTrader)
                {
                    if (tab.Security.UsePriceStepCostToCalculateVolume == true
                        && tab.Security.PriceStep != tab.Security.PriceStepCost
                        && tab.PriceBestAsk != 0
                        && tab.Security.PriceStep != 0
                        && tab.Security.PriceStepCost != 0)
                    {
                        qty = moneyOnPosition / (tab.PriceBestAsk / tab.Security.PriceStep * tab.Security.PriceStepCost);
                    }

                    qty = Math.Round(qty, tab.Security.DecimalsVolume);
                }
                else
                {
                    qty = Math.Round(qty, 7);
                }

                return qty;
            }

            return volume;
        }
    }
}
