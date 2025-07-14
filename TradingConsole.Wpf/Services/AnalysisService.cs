﻿// In TradingConsole.Wpf/Services/AnalysisService.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TradingConsole.Core.Models;
using TradingConsole.DhanApi;
using TradingConsole.DhanApi.Models;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Wpf.Services
{
    public class AnalysisService : INotifyPropertyChanged
    {
        #region Parameters and State
        private readonly SettingsViewModel _settingsViewModel;
        private readonly DhanApiClient _apiClient;
        private readonly ScripMasterService _scripMasterService;
        private readonly HistoricalIvService _historicalIvService;
        private readonly MarketProfileService _marketProfileService;
        private readonly Dictionary<string, List<MarketProfileData>> _historicalMarketProfiles = new Dictionary<string, List<MarketProfileData>>();

        private readonly Dictionary<string, IntradayIvState.CustomLevelState> _customLevelStates = new();
        private readonly HashSet<string> _backfilledInstruments = new HashSet<string>();
        private readonly Dictionary<string, AnalysisResult> _analysisResults = new();
        private readonly Dictionary<string, MarketProfile> _marketProfiles = new Dictionary<string, MarketProfile>();
        private readonly Dictionary<string, (bool isBreakout, bool isBreakdown)> _initialBalanceState = new();


        public int ShortEmaLength { get; set; }
        public int LongEmaLength { get; set; }
        public int AtrPeriod { get; set; }
        public int AtrSmaPeriod { get; set; }
        public int RsiPeriod { get; set; }
        public int RsiDivergenceLookback { get; set; }
        public int VolumeHistoryLength { get; set; }
        public double VolumeBurstMultiplier { get; set; }
        public int IvHistoryLength { get; set; }
        public decimal IvSpikeThreshold { get; set; }
        public int ObvMovingAveragePeriod { get; set; }

        private const int MinIvHistoryForSignal = 2;
        private readonly List<TimeSpan> _timeframes = new()
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)
        };
        private readonly Dictionary<string, (decimal cumulativePriceVolume, long cumulativeVolume, List<decimal> ivHistory)> _tickAnalysisState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, List<Candle>>> _multiTimeframeCandles = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, EmaState>> _multiTimeframePriceEmaState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, EmaState>> _multiTimeframeVwapEmaState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, RsiState>> _multiTimeframeRsiState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, AtrState>> _multiTimeframeAtrState = new();
        private readonly Dictionary<string, Dictionary<TimeSpan, ObvState>> _multiTimeframeObvState = new();
        private readonly Dictionary<string, IntradayIvState> _intradayIvStates = new Dictionary<string, IntradayIvState>();

        public event Action<AnalysisResult>? OnAnalysisUpdated;

        public event Action<string, Candle, TimeSpan>? CandleUpdated;
        #endregion

        public AnalysisService(SettingsViewModel settingsViewModel, DhanApiClient apiClient, ScripMasterService scripMasterService, HistoricalIvService historicalIvService, MarketProfileService marketProfileService)
        {
            _settingsViewModel = settingsViewModel;
            _apiClient = apiClient;
            _scripMasterService = scripMasterService;
            _historicalIvService = historicalIvService;
            _marketProfileService = marketProfileService;

            UpdateParametersFromSettings();
        }

        public void SaveMarketProfileDatabase()
        {
            _marketProfileService.SaveDatabase();
        }

        public void UpdateParametersFromSettings()
        {
            ShortEmaLength = _settingsViewModel.ShortEmaLength;
            LongEmaLength = _settingsViewModel.LongEmaLength;
            AtrPeriod = _settingsViewModel.AtrPeriod;
            AtrSmaPeriod = _settingsViewModel.AtrSmaPeriod;
            RsiPeriod = _settingsViewModel.RsiPeriod;
            RsiDivergenceLookback = _settingsViewModel.RsiDivergenceLookback;
            VolumeHistoryLength = _settingsViewModel.VolumeHistoryLength;
            VolumeBurstMultiplier = _settingsViewModel.VolumeBurstMultiplier;
            IvHistoryLength = _settingsViewModel.IvHistoryLength;
            IvSpikeThreshold = _settingsViewModel.IvSpikeThreshold;
            ObvMovingAveragePeriod = _settingsViewModel.ObvMovingAveragePeriod;
        }

        public List<Candle>? GetCandles(string securityId, TimeSpan timeframe)
        {
            if (_multiTimeframeCandles.TryGetValue(securityId, out var timeframes) &&
                timeframes.TryGetValue(timeframe, out var candles))
            {
                return candles;
            }
            return null;
        }

        public async void OnInstrumentDataReceived(DashboardInstrument instrument, decimal underlyingPrice)
        {
            if (string.IsNullOrEmpty(instrument.SecurityId)) return;

            if (instrument.InstrumentType.StartsWith("OPT") && instrument.ImpliedVolatility > 0)
            {
                var ivKey = GetHistoricalIvKey(instrument, underlyingPrice);
                if (!string.IsNullOrEmpty(ivKey))
                {
                    if (!_intradayIvStates.ContainsKey(ivKey))
                    {
                        _intradayIvStates[ivKey] = new IntradayIvState();
                    }
                    var ivState = _intradayIvStates[ivKey];

                    ivState.DayHighIv = Math.Max(ivState.DayHighIv, instrument.ImpliedVolatility);
                    ivState.DayLowIv = Math.Min(ivState.DayLowIv, instrument.ImpliedVolatility);

                    _historicalIvService.RecordDailyIv(ivKey, ivState.DayHighIv, ivState.DayLowIv);

                    var (ivRank, ivPercentile) = CalculateIvRankAndPercentile(instrument.ImpliedVolatility, ivKey, ivState);
                    var ivTrendSignal = GetIvTrendSignal(ivPercentile, ivRank, ivState);

                    if (_analysisResults.TryGetValue(instrument.SecurityId, out var existingResult))
                    {
                        existingResult.IvRank = ivRank;
                        existingResult.IvPercentile = ivPercentile;
                        existingResult.IvTrendSignal = ivTrendSignal;
                    }
                }
            }

            bool isNewInstrument = !_backfilledInstruments.Contains(instrument.SecurityId);
            if (isNewInstrument)
            {
                _backfilledInstruments.Add(instrument.SecurityId);

                _tickAnalysisState[instrument.SecurityId] = (0, 0, new List<decimal>());
                _multiTimeframeCandles[instrument.SecurityId] = new Dictionary<TimeSpan, List<Candle>>();
                _multiTimeframePriceEmaState[instrument.SecurityId] = new Dictionary<TimeSpan, EmaState>();
                _multiTimeframeVwapEmaState[instrument.SecurityId] = new Dictionary<TimeSpan, EmaState>();
                _multiTimeframeRsiState[instrument.SecurityId] = new Dictionary<TimeSpan, RsiState>();
                _multiTimeframeAtrState[instrument.SecurityId] = new Dictionary<TimeSpan, AtrState>();
                _multiTimeframeObvState[instrument.SecurityId] = new Dictionary<TimeSpan, ObvState>();

                _historicalMarketProfiles[instrument.SecurityId] = _marketProfileService.GetHistoricalProfiles(instrument.SecurityId);

                if (!_marketProfiles.ContainsKey(instrument.SecurityId))
                {
                    decimal tickSize = GetTickSize(instrument);
                    var startTime = DateTime.Today.Add(new TimeSpan(9, 15, 0));
                    _marketProfiles[instrument.SecurityId] = new MarketProfile(tickSize, startTime);
                }

                foreach (var tf in _timeframes)
                {
                    _multiTimeframeCandles[instrument.SecurityId][tf] = new List<Candle>();
                    _multiTimeframePriceEmaState[instrument.SecurityId][tf] = new EmaState();
                    _multiTimeframeVwapEmaState[instrument.SecurityId][tf] = new EmaState();
                    _multiTimeframeRsiState[instrument.SecurityId][tf] = new RsiState();
                    _multiTimeframeAtrState[instrument.SecurityId][tf] = new AtrState();
                    _multiTimeframeObvState[instrument.SecurityId][tf] = new ObvState();
                }

                if (instrument.SegmentId == 0)
                {
                    _customLevelStates[instrument.Symbol] = new IntradayIvState.CustomLevelState();
                }

                await BackfillAndSavePreviousDayProfileAsync(instrument);
                await BackfillCurrentDayCandlesAsync(instrument);
                RunDailyBiasAnalysis(instrument);
            }

            foreach (var timeframe in _timeframes)
            {
                AggregateIntoCandle(instrument, timeframe);
            }

            RunComplexAnalysis(instrument);
        }

        private DateTime GetPreviousTradingDay(DateTime currentDate)
        {
            var date = currentDate.Date.AddDays(-1);
            while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday || _settingsViewModel.MarketHolidays.Contains(date))
            {
                date = date.AddDays(-1);
            }
            return date;
        }

        private async Task BackfillAndSavePreviousDayProfileAsync(DashboardInstrument instrument)
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);

            if (istNow.TimeOfDay >= new TimeSpan(9, 15, 0))
            {
                Debug.WriteLine($"[BackfillPrevDay] Skipping, market is open. Current time: {istNow.TimeOfDay}");
                return;
            }

            DateTime dateToFetch = GetPreviousTradingDay(istNow);

            if (_historicalMarketProfiles.GetValueOrDefault(instrument.SecurityId)?.Any(p => p.Date.Date == dateToFetch.Date) == true)
            {
                Debug.WriteLine($"[BackfillPrevDay] Profile for {instrument.DisplayName} on {dateToFetch:yyyy-MM-dd} already exists. Skipping fetch.");
                return;
            }

            Debug.WriteLine($"[BackfillPrevDay] Starting backfill process for {instrument.DisplayName} for date: {dateToFetch:yyyy-MM-dd}.");

            try
            {
                var scripInfo = _scripMasterService.FindBySecurityIdAndType(instrument.SecurityId, instrument.InstrumentType);
                if (scripInfo == null)
                {
                    Debug.WriteLine($"[BackfillPrevDay] FAILED: Could not find scrip info for {instrument.SecurityId}.");
                    return;
                }

                var historicalData = await _apiClient.GetIntradayHistoricalDataAsync(scripInfo, "1", dateToFetch);

                if (historicalData?.Open == null || !historicalData.Open.Any())
                {
                    Debug.WriteLine($"[BackfillPrevDay] No historical data points returned from API for {instrument.DisplayName} for date {dateToFetch:yyyy-MM-dd}.");
                    return;
                }

                Debug.WriteLine($"[BackfillPrevDay] SUCCESS: Received {historicalData.Open.Count} historical data points for {instrument.DisplayName}.");

                decimal tickSize = GetTickSize(instrument);
                var sessionStartTime = dateToFetch.Date.Add(new TimeSpan(9, 15, 0));
                var historicalProfile = new MarketProfile(tickSize, sessionStartTime);

                for (int i = 0; i < historicalData.Open.Count; i++)
                {
                    var candle = new Candle
                    {
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)historicalData.StartTime[i]).UtcDateTime,
                        Open = historicalData.Open[i],
                        High = historicalData.High[i],
                        Low = historicalData.Low[i],
                        Close = historicalData.Close[i],
                        Volume = (long)historicalData.Volume[i],
                        OpenInterest = historicalData.OpenInterest.Count > i ? (long)historicalData.OpenInterest[i] : 0,
                    };
                    UpdateMarketProfile(historicalProfile, candle);
                }

                CalculateDevelopingProfileLevels(historicalProfile);
                var profileDataToSave = historicalProfile.ToMarketProfileData();
                _marketProfileService.UpdateProfile(instrument.SecurityId, profileDataToSave);

                Debug.WriteLine($"[BackfillPrevDay] Successfully built and saved historical market profile for {instrument.DisplayName} for {dateToFetch:yyyy-MM-dd}.");

                if (!_historicalMarketProfiles.ContainsKey(instrument.SecurityId))
                {
                    _historicalMarketProfiles[instrument.SecurityId] = new List<MarketProfileData>();
                }
                _historicalMarketProfiles[instrument.SecurityId].RemoveAll(p => p.Date.Date == dateToFetch.Date);
                _historicalMarketProfiles[instrument.SecurityId].Add(profileDataToSave);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackfillPrevDay] UNEXPECTED ERROR during backfill for {instrument.DisplayName}: {ex.Message}");
            }
        }

        private async Task BackfillCurrentDayCandlesAsync(DashboardInstrument instrument)
        {
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);

            if (istNow.TimeOfDay < new TimeSpan(9, 15, 0)) return;

            Debug.WriteLine($"[BackfillCurrentDay] App started mid-day. Backfilling today's candles for {instrument.DisplayName}.");

            try
            {
                var scripInfo = _scripMasterService.FindBySecurityIdAndType(instrument.SecurityId, instrument.InstrumentType);
                if (scripInfo == null) return;

                var historicalData = await _apiClient.GetIntradayHistoricalDataAsync(scripInfo, "1", istNow.Date);

                if (historicalData?.Open != null && historicalData.Open.Any())
                {
                    var candles = new List<Candle>();
                    for (int i = 0; i < historicalData.Open.Count; i++)
                    {
                        var candle = new Candle
                        {
                            Timestamp = DateTimeOffset.FromUnixTimeSeconds((long)historicalData.StartTime[i]).UtcDateTime,
                            Open = historicalData.Open[i],
                            High = historicalData.High[i],
                            Low = historicalData.Low[i],
                            Close = historicalData.Close[i],
                            Volume = (long)historicalData.Volume[i],
                            OpenInterest = historicalData.OpenInterest.Count > i ? (long)historicalData.OpenInterest[i] : 0,
                        };
                        candles.Add(candle);

                        if (_marketProfiles.TryGetValue(instrument.SecurityId, out var liveProfile))
                        {
                            UpdateMarketProfile(liveProfile, candle);
                        }
                    }

                    foreach (var timeframe in _timeframes)
                    {
                        var aggregatedCandles = AggregateHistoricalCandles(candles, timeframe);
                        _multiTimeframeCandles[instrument.SecurityId][timeframe] = aggregatedCandles;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackfillCurrentDay] ERROR: {ex.Message}");
            }
        }

        #region Market Profile (TPO) and Volume Profile Calculation
        private void UpdateMarketProfile(MarketProfile profile, Candle candle)
        {
            profile.UpdateInitialBalance(candle);
            var tpoPeriod = profile.GetTpoPeriod(candle.Timestamp);

            // This loop is for TPOs only
            for (decimal price = candle.Low; price <= candle.High; price += profile.TickSize)
            {
                var quantizedPrice = profile.QuantizePrice(price);
                if (!profile.TpoLevels.ContainsKey(quantizedPrice))
                {
                    profile.TpoLevels[quantizedPrice] = new List<char>();
                }
                if (!profile.TpoLevels[quantizedPrice].Contains(tpoPeriod))
                {
                    profile.TpoLevels[quantizedPrice].Add(tpoPeriod);
                }
            }

            var typicalPrice = (candle.High + candle.Low + candle.Close) / 3;
            var typicalPriceQuantized = profile.QuantizePrice(typicalPrice);
            if (!profile.VolumeLevels.ContainsKey(typicalPriceQuantized))
            {
                profile.VolumeLevels[typicalPriceQuantized] = 0;
            }
            profile.VolumeLevels[typicalPriceQuantized] += candle.Volume;
        }

        private void UpdateMarketProfile(string securityId, Candle candle)
        {
            if (!_marketProfiles.TryGetValue(securityId, out var profile))
            {
                return;
            }
            UpdateMarketProfile(profile, candle);
            CalculateDevelopingProfileLevels(profile);
        }

        private void CalculateDevelopingProfileLevels(MarketProfile profile)
        {
            if (profile.TpoLevels.Count == 0) return;

            var pocLevel = profile.TpoLevels
                .OrderByDescending(kvp => kvp.Value.Count)
                .ThenBy(kvp => Math.Abs(kvp.Key - profile.DevelopingTpoLevels.PointOfControl))
                .FirstOrDefault();

            if (pocLevel.Key == 0) return;

            profile.DevelopingTpoLevels.PointOfControl = pocLevel.Key;

            long totalTpos = profile.TpoLevels.Sum(kvp => kvp.Value.Count);
            long tposInVaTarget = (long)(totalTpos * 0.70);

            var valueAreaLevels = new List<KeyValuePair<decimal, List<char>>> { pocLevel };
            long tposInVaCurrent = pocLevel.Value.Count;

            var levelsAbovePoc = profile.TpoLevels.Where(kvp => kvp.Key > pocLevel.Key).OrderBy(kvp => kvp.Key).ToList();
            var levelsBelowPoc = profile.TpoLevels.Where(kvp => kvp.Key < pocLevel.Key).OrderByDescending(kvp => kvp.Key).ToList();

            int aboveIndex = 0;
            int belowIndex = 0;

            while (tposInVaCurrent < tposInVaTarget && (aboveIndex < levelsAbovePoc.Count || belowIndex < levelsBelowPoc.Count))
            {
                long tpoCountAbove = (aboveIndex < levelsAbovePoc.Count) ? levelsAbovePoc[aboveIndex].Value.Count : 0;
                long tpoCountBelow = (belowIndex < levelsBelowPoc.Count) ? levelsBelowPoc[belowIndex].Value.Count : 0;

                if (tpoCountAbove > tpoCountBelow)
                {
                    valueAreaLevels.Add(levelsAbovePoc[aboveIndex]);
                    tposInVaCurrent += tpoCountAbove;
                    aboveIndex++;
                }
                else if (tpoCountBelow > 0)
                {
                    valueAreaLevels.Add(levelsBelowPoc[belowIndex]);
                    tposInVaCurrent += tpoCountBelow;
                    belowIndex++;
                }
                else
                {
                    break;
                }
            }

            if (valueAreaLevels.Any())
            {
                profile.DevelopingTpoLevels.ValueAreaHigh = valueAreaLevels.Max(kvp => kvp.Key);
                profile.DevelopingTpoLevels.ValueAreaLow = valueAreaLevels.Min(kvp => kvp.Key);
            }

            if (profile.VolumeLevels.Count == 0) return;

            var vpocLevel = profile.VolumeLevels
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .FirstOrDefault();

            if (vpocLevel.Key != 0)
            {
                profile.DevelopingVolumeProfile.VolumePoc = vpocLevel.Key;
            }
        }

        public void RunDailyBiasAnalysis(DashboardInstrument instrument)
        {
            if (!_historicalMarketProfiles.TryGetValue(instrument.SecurityId, out var profiles) || profiles.Count < 2)
            {
                if (_analysisResults.TryGetValue(instrument.SecurityId, out var result))
                {
                    result.DailyBias = "Insufficient History";
                }
                return;
            }

            var recentProfiles = profiles.OrderByDescending(p => p.Date).Take(8).ToList();
            var previousDay = recentProfiles.FirstOrDefault(p => p.Date.Date < DateTime.Today);
            if (previousDay == null) return;

            string structure = AnalyzeMarketStructure(recentProfiles);
            string openingBias = AnalyzeOpeningCondition(instrument.Open, previousDay);
            string finalBias = SynthesizeBias(structure, openingBias);

            if (_analysisResults.TryGetValue(instrument.SecurityId, out var analysisResult))
            {
                analysisResult.MarketStructure = structure;
                analysisResult.DailyBias = finalBias;
                OnAnalysisUpdated?.Invoke(analysisResult);
            }
        }

        private string AnalyzeMarketStructure(List<MarketProfileData> profiles)
        {
            if (profiles.Count < 3) return "Building";

            var lastThreeDays = profiles.Take(3).ToList();
            var day1 = lastThreeDays[0];
            var day2 = lastThreeDays[1];
            var day3 = lastThreeDays[2];

            bool isTrendingUp = day1.TpoLevelsInfo.ValueAreaLow > day2.TpoLevelsInfo.ValueAreaLow &&
                                day2.TpoLevelsInfo.ValueAreaLow > day3.TpoLevelsInfo.ValueAreaLow;

            bool isTrendingDown = day1.TpoLevelsInfo.ValueAreaHigh < day2.TpoLevelsInfo.ValueAreaHigh &&
                                  day2.TpoLevelsInfo.ValueAreaHigh < day3.TpoLevelsInfo.ValueAreaHigh;

            if (isTrendingUp) return "Trending Up";
            if (isTrendingDown) return "Trending Down";

            bool isOverlapping = (day1.TpoLevelsInfo.ValueAreaHigh >= day2.TpoLevelsInfo.ValueAreaLow) &&
                                 (day1.TpoLevelsInfo.ValueAreaLow <= day2.TpoLevelsInfo.ValueAreaHigh);

            if (isOverlapping) return "Balancing";

            return "Transitioning";
        }

        private string AnalyzeOpeningCondition(decimal openPrice, MarketProfileData previousDay)
        {
            if (openPrice == 0) return "Awaiting Open";

            var prevVAH = previousDay.TpoLevelsInfo.ValueAreaHigh;
            var prevVAL = previousDay.TpoLevelsInfo.ValueAreaLow;
            var prevPOC = previousDay.TpoLevelsInfo.PointOfControl;

            if (openPrice > prevVAH) return "Opening Above Value";
            if (openPrice < prevVAL) return "Opening Below Value";
            if (openPrice > prevPOC) return "Opening Inside Value (High)";
            if (openPrice < prevPOC) return "Opening Inside Value (Low)";

            return "Opening at POC";
        }

        private string SynthesizeBias(string structure, string opening)
        {
            if (opening == "Awaiting Open") return "Awaiting Open";

            if (structure == "Trending Up" && opening == "Opening Above Value") return "Strong Bullish";
            if (structure == "Trending Down" && opening == "Opening Below Value") return "Strong Bearish";
            if (structure == "Trending Up" && opening.Contains("Inside Value")) return "Bullish Rotational";
            if (structure == "Trending Down" && opening.Contains("Inside Value")) return "Bearish Rotational";
            if (structure == "Balancing" && opening == "Opening Above Value") return "Bullish Breakout Watch";
            if (structure == "Balancing" && opening == "Opening Below Value") return "Bearish Breakout Watch";
            if (structure == "Balancing" && opening.Contains("Inside Value")) return "Pure Rotational";

            return "Neutral";
        }

        private string GetMarketProfileSignal(decimal ltp, MarketProfile? currentProfile, List<MarketProfileData>? historicalProfiles, DashboardInstrument instrument)
        {
            if (currentProfile == null || ltp == 0) return "Building";

            var previousDayProfile = historicalProfiles?.FirstOrDefault(p => p.Date.Date < DateTime.Today.Date);

            if (previousDayProfile != null)
            {
                var prevVAH = previousDayProfile.TpoLevelsInfo.ValueAreaHigh;
                var prevVAL = previousDayProfile.TpoLevelsInfo.ValueAreaLow;

                if (ltp > prevVAH && currentProfile.DevelopingTpoLevels.ValueAreaLow > prevVAH) return "Acceptance > Y-VAH";
                if (ltp < prevVAL && currentProfile.DevelopingTpoLevels.ValueAreaHigh < prevVAL) return "Acceptance < Y-VAL";
                if (ltp > prevVAH && currentProfile.DevelopingTpoLevels.PointOfControl < prevVAH) return "Rejection at Y-VAH";
            }

            string ibSignal = GetInitialBalanceSignal(ltp, currentProfile, instrument.SecurityId);
            if (ibSignal != "Inside IB")
            {
                return ibSignal;
            }

            return GetBaseMarketSignal(ltp, currentProfile);
        }

        private string GetInitialBalanceSignal(decimal ltp, MarketProfile profile, string securityId)
        {
            if (!profile.IsInitialBalanceSet)
            {
                return "IB Forming";
            }

            if (!_initialBalanceState.ContainsKey(securityId))
            {
                _initialBalanceState[securityId] = (false, false);
            }

            var (isBreakout, isBreakdown) = _initialBalanceState[securityId];

            if (ltp > profile.InitialBalanceHigh && !isBreakout)
            {
                _initialBalanceState[securityId] = (true, false);
                return "IB Breakout";
            }

            if (ltp < profile.InitialBalanceLow && !isBreakdown)
            {
                _initialBalanceState[securityId] = (false, true);
                return "IB Breakdown";
            }

            if (ltp > profile.InitialBalanceHigh && isBreakout) return "IB Extension Up";
            if (ltp < profile.InitialBalanceLow && isBreakdown) return "IB Extension Down";

            if (ltp <= profile.InitialBalanceHigh && isBreakout)
            {
                _initialBalanceState[securityId] = (false, false);
                return "IB Failed Breakout";
            }

            if (ltp >= profile.InitialBalanceLow && isBreakdown)
            {
                _initialBalanceState[securityId] = (false, false);
                return "IB Failed Breakdown";
            }

            return "Inside IB";
        }


        private string GetBaseMarketSignal(decimal ltp, MarketProfile profile)
        {
            var tpoInfo = profile.DevelopingTpoLevels;
            var volumeInfo = profile.DevelopingVolumeProfile;
            decimal tolerance = ltp * 0.0002m;

            var vahUpperBand = tpoInfo.ValueAreaHigh + tolerance;
            var vahLowerBand = tpoInfo.ValueAreaHigh - tolerance;
            var valUpperBand = tpoInfo.ValueAreaLow + tolerance;
            var valLowerBand = tpoInfo.ValueAreaLow - tolerance;
            var pocUpperBand = tpoInfo.PointOfControl + tolerance;
            var pocLowerBand = tpoInfo.PointOfControl - tolerance;
            var vpocUpperBand = volumeInfo.VolumePoc + tolerance;
            var vpocLowerBand = volumeInfo.VolumePoc - tolerance;

            if (ltp > vahUpperBand) return "Breakout above value";
            if (ltp < valLowerBand) return "Breakdown below value";
            if (ltp >= vahLowerBand && ltp <= vahUpperBand) return "At VAH Band";
            if (ltp >= valLowerBand && ltp <= valUpperBand) return "At VAL Band";

            bool inPocBand = ltp >= pocLowerBand && ltp <= pocUpperBand;
            bool inVpocBand = volumeInfo.VolumePoc > 0 && (ltp >= vpocLowerBand && ltp <= vpocUpperBand);

            if (inPocBand && inVpocBand) return "At POC & VPOC - High conviction";
            if (inPocBand) return "At POC Band";
            if (inVpocBand) return "At VPOC Band";

            return "Inside Value Area";
        }

        private decimal GetTickSize(DashboardInstrument? instrument)
        {
            if (instrument?.InstrumentType == "INDEX")
            {
                return 1.0m;
            }
            return 0.05m;
        }

        #endregion

        private string GetHistoricalIvKey(DashboardInstrument instrument, decimal underlyingPrice)
        {
            if (string.IsNullOrEmpty(instrument.UnderlyingSymbol)) return string.Empty;

            var scripInfo = _scripMasterService.FindBySecurityId(instrument.SecurityId);
            if (scripInfo == null || scripInfo.StrikePrice <= 0) return string.Empty;

            int strikeDistance = (int)Math.Round((scripInfo.StrikePrice - underlyingPrice) / 50);
            string moneyness;
            if (strikeDistance == 0) moneyness = "ATM";
            else if (strikeDistance > 0) moneyness = $"ATM+{strikeDistance}";
            else moneyness = $"ATM{strikeDistance}";

            return $"{instrument.UnderlyingSymbol}_{moneyness}_{scripInfo.OptionType}";
        }

        private (decimal ivRank, decimal ivPercentile) CalculateIvRankAndPercentile(decimal currentIv, string key, IntradayIvState ivState)
        {
            decimal dayRange = ivState.DayHighIv - ivState.DayLowIv;
            decimal ivPercentile = (dayRange > 0) ? (currentIv - ivState.DayLowIv) / dayRange * 100 : 0;

            var (histHigh, histLow) = _historicalIvService.Get90DayIvRange(key);
            decimal histRange = histHigh - histLow;
            decimal ivRank = (histRange > 0) ? (currentIv - histLow) / histRange * 100 : 0;

            return (Math.Round(ivRank, 2), Math.Round(ivPercentile, 2));
        }

        private string GetIvTrendSignal(decimal ivp, decimal ivr, IntradayIvState state)
        {
            state.IvPercentileHistory.Add(ivp);
            if (state.IvPercentileHistory.Count > 10)
            {
                state.IvPercentileHistory.RemoveAt(0);
            }

            if (state.IvPercentileHistory.Count < 5)
            {
                return "Building History...";
            }

            var recentIVP = state.IvPercentileHistory.Last();
            var previousIVP = state.IvPercentileHistory[^2];
            var fivePeriodAvgIVP = state.IvPercentileHistory.TakeLast(5).Average();
            var tenPeriodAvgIVP = state.IvPercentileHistory.Average();

            if (recentIVP > previousIVP + 15 && recentIVP > 60) return "IV Spike Up";
            if (recentIVP < previousIVP - 15 && recentIVP < 40) return "IV Contraction";
            if (ivr > 85 && recentIVP < fivePeriodAvgIVP && recentIVP < tenPeriodAvgIVP) return "IV Crush Warning";
            if (ivr < 60 && recentIVP > fivePeriodAvgIVP && previousIVP < tenPeriodAvgIVP) return "IV Rising (Momentum)";
            if (ivr < 20 && ivp < 20) return "IV Low & Stable";

            return "Neutral";
        }

        private List<Candle> AggregateHistoricalCandles(List<Candle> minuteCandles, TimeSpan timeframe)
        {
            return minuteCandles
                .GroupBy(c => new DateTime(c.Timestamp.Ticks - (c.Timestamp.Ticks % timeframe.Ticks), DateTimeKind.Utc))
                .Select(g => new Candle
                {
                    Timestamp = g.Key,
                    Open = g.First().Open,
                    High = g.Max(c => c.High),
                    Low = g.Min(c => c.Low),
                    Close = g.Last().Close,
                    Volume = g.Sum(c => c.Volume),
                    OpenInterest = g.Last().OpenInterest,
                    Vwap = g.Sum(c => c.Close * c.Volume) / (g.Sum(c => c.Volume) == 0 ? 1 : g.Sum(c => c.Volume))
                })
                .ToList();
        }

        private void AggregateIntoCandle(DashboardInstrument instrument, TimeSpan timeframe)
        {
            if (!_multiTimeframeCandles.ContainsKey(instrument.SecurityId) || !_multiTimeframeCandles[instrument.SecurityId].ContainsKey(timeframe))
            {
                return;
            }

            var candles = _multiTimeframeCandles[instrument.SecurityId][timeframe];
            var now = DateTime.UtcNow;
            var candleTimestamp = new DateTime(now.Ticks - (now.Ticks % timeframe.Ticks), now.Kind);

            var currentCandle = candles.LastOrDefault();
            Candle? candleToNotify = null;

            if (currentCandle == null || currentCandle.Timestamp != candleTimestamp)
            {
                var lastCandleInList = candles.LastOrDefault();
                if (lastCandleInList != null && candleTimestamp.Date > lastCandleInList.Timestamp.Date)
                {
                    candles.Clear();
                    _multiTimeframePriceEmaState[instrument.SecurityId][timeframe] = new EmaState();
                    _multiTimeframeVwapEmaState[instrument.SecurityId][timeframe] = new EmaState();
                    _multiTimeframeRsiState[instrument.SecurityId][timeframe] = new RsiState();
                    _multiTimeframeAtrState[instrument.SecurityId][timeframe] = new AtrState();
                    _multiTimeframeObvState[instrument.SecurityId][timeframe] = new ObvState();
                    _tickAnalysisState[instrument.SecurityId] = (0, 0, new List<decimal>());

                    decimal tickSize = GetTickSize(instrument);
                    var startTime = DateTime.Today.Add(new TimeSpan(9, 15, 0));
                    _marketProfiles[instrument.SecurityId] = new MarketProfile(tickSize, startTime);
                }

                var newCandle = new Candle
                {
                    Timestamp = candleTimestamp,
                    Open = instrument.LTP,
                    High = instrument.LTP,
                    Low = instrument.LTP,
                    Close = instrument.LTP,
                    Volume = instrument.LastTradedQuantity,
                    OpenInterest = instrument.OpenInterest,
                    CumulativePriceVolume = instrument.AvgTradePrice * instrument.LastTradedQuantity,
                    CumulativeVolume = instrument.LastTradedQuantity,
                    Vwap = instrument.AvgTradePrice
                };
                candles.Add(newCandle);
                candleToNotify = newCandle;

                if (timeframe.TotalMinutes == 1)
                {
                    UpdateMarketProfile(instrument.SecurityId, newCandle);
                }
            }
            else
            {
                currentCandle.High = Math.Max(currentCandle.High, instrument.LTP);
                currentCandle.Low = Math.Min(currentCandle.Low, instrument.LTP);
                currentCandle.Close = instrument.LTP;
                currentCandle.Volume += instrument.LastTradedQuantity;
                currentCandle.OpenInterest = instrument.OpenInterest;
                currentCandle.CumulativePriceVolume += instrument.AvgTradePrice * instrument.LastTradedQuantity;
                currentCandle.CumulativeVolume += instrument.LastTradedQuantity;
                currentCandle.Vwap = (currentCandle.CumulativeVolume > 0)
                    ? currentCandle.CumulativePriceVolume / currentCandle.CumulativeVolume
                    : currentCandle.Close;
                candleToNotify = currentCandle;
            }

            if (candleToNotify != null)
            {
                CandleUpdated?.Invoke(instrument.SecurityId, candleToNotify, timeframe);
            }
        }

        private void RunComplexAnalysis(DashboardInstrument instrument)
        {
            if (!_analysisResults.TryGetValue(instrument.SecurityId, out var result))
            {
                result = new AnalysisResult { SecurityId = instrument.SecurityId };
                _analysisResults[instrument.SecurityId] = result;
            }

            var tickState = _tickAnalysisState[instrument.SecurityId];
            tickState.cumulativePriceVolume += instrument.AvgTradePrice * instrument.LastTradedQuantity;
            tickState.cumulativeVolume += instrument.LastTradedQuantity;
            decimal dayVwap = (tickState.cumulativeVolume > 0) ? tickState.cumulativePriceVolume / tickState.cumulativeVolume : 0;

            if (instrument.ImpliedVolatility > 0) tickState.ivHistory.Add(instrument.ImpliedVolatility);
            if (tickState.ivHistory.Count > this.IvHistoryLength) tickState.ivHistory.RemoveAt(0);
            var (avgIv, ivSignal) = CalculateIvSignal(instrument.ImpliedVolatility, tickState.ivHistory);

            _tickAnalysisState[instrument.SecurityId] = tickState;

            var oneMinCandles = _multiTimeframeCandles[instrument.SecurityId].GetValueOrDefault(TimeSpan.FromMinutes(1));
            var fiveMinCandles = _multiTimeframeCandles[instrument.SecurityId].GetValueOrDefault(TimeSpan.FromMinutes(5));

            if (oneMinCandles != null)
            {
                result.RsiValue1Min = CalculateRsi(oneMinCandles, _multiTimeframeRsiState[instrument.SecurityId][TimeSpan.FromMinutes(1)], this.RsiPeriod);
                result.RsiSignal1Min = DetectRsiDivergence(oneMinCandles, _multiTimeframeRsiState[instrument.SecurityId][TimeSpan.FromMinutes(1)], this.RsiDivergenceLookback);
                result.Atr1Min = CalculateAtr(oneMinCandles, _multiTimeframeAtrState[instrument.SecurityId][TimeSpan.FromMinutes(1)], this.AtrPeriod);
                result.AtrSignal1Min = GetAtrSignal(result.Atr1Min, _multiTimeframeAtrState[instrument.SecurityId][TimeSpan.FromMinutes(1)], this.AtrSmaPeriod);
                result.ObvValue1Min = CalculateObv(oneMinCandles, _multiTimeframeObvState[instrument.SecurityId][TimeSpan.FromMinutes(1)]);
                result.ObvSignal1Min = CalculateObvSignal(oneMinCandles, _multiTimeframeObvState[instrument.SecurityId][TimeSpan.FromMinutes(1)], this.ObvMovingAveragePeriod);
                result.ObvDivergenceSignal1Min = DetectObvDivergence(oneMinCandles, _multiTimeframeObvState[instrument.SecurityId][TimeSpan.FromMinutes(1)], this.RsiDivergenceLookback);
            }
            if (fiveMinCandles != null)
            {
                result.RsiValue5Min = CalculateRsi(fiveMinCandles, _multiTimeframeRsiState[instrument.SecurityId][TimeSpan.FromMinutes(5)], this.RsiPeriod);
                result.RsiSignal5Min = DetectRsiDivergence(fiveMinCandles, _multiTimeframeRsiState[instrument.SecurityId][TimeSpan.FromMinutes(5)], this.RsiDivergenceLookback);
                result.Atr5Min = CalculateAtr(fiveMinCandles, _multiTimeframeAtrState[instrument.SecurityId][TimeSpan.FromMinutes(5)], this.AtrPeriod);
                result.AtrSignal5Min = GetAtrSignal(result.Atr5Min, _multiTimeframeAtrState[instrument.SecurityId][TimeSpan.FromMinutes(5)], this.AtrSmaPeriod);
                result.ObvValue5Min = CalculateObv(fiveMinCandles, _multiTimeframeObvState[instrument.SecurityId][TimeSpan.FromMinutes(5)]);
                result.ObvSignal5Min = CalculateObvSignal(fiveMinCandles, _multiTimeframeObvState[instrument.SecurityId][TimeSpan.FromMinutes(5)], this.ObvMovingAveragePeriod);
                result.ObvDivergenceSignal5Min = DetectObvDivergence(fiveMinCandles, _multiTimeframeObvState[instrument.SecurityId][TimeSpan.FromMinutes(5)], this.RsiDivergenceLookback);
            }

            var priceEmaSignals = new Dictionary<TimeSpan, string>();
            var vwapEmaSignals = new Dictionary<TimeSpan, string>();
            foreach (var timeframe in _timeframes)
            {
                var candles = _multiTimeframeCandles[instrument.SecurityId].GetValueOrDefault(timeframe);
                if (candles == null || !candles.Any()) continue;
                priceEmaSignals[timeframe] = CalculateEmaSignal(instrument.SecurityId, candles, _multiTimeframePriceEmaState, useVwap: false);
                vwapEmaSignals[timeframe] = CalculateEmaSignal(instrument.SecurityId, candles, _multiTimeframeVwapEmaState, useVwap: true);
            }

            var (volumeSignal, currentCandleVolume, avgCandleVolume) = ("Neutral", 0L, 0L);
            if (oneMinCandles != null && oneMinCandles.Any())
            {
                (volumeSignal, currentCandleVolume, avgCandleVolume) = CalculateVolumeSignalForTimeframe(oneMinCandles);
            }

            string oiSignal = "N/A";
            if (oneMinCandles != null && oneMinCandles.Any())
            {
                oiSignal = CalculateOiSignal(oneMinCandles);
            }

            var paSignals = CalculatePriceActionSignals(instrument, dayVwap);
            string customLevelSignal = CalculateCustomLevelSignal(instrument);
            string candleSignal1Min = "N/A";
            if (oneMinCandles != null) candleSignal1Min = RecognizeCandlestickPattern(oneMinCandles);
            string candleSignal5Min = "N/A";
            if (fiveMinCandles != null) candleSignal5Min = RecognizeCandlestickPattern(fiveMinCandles);

            if (_marketProfiles.TryGetValue(instrument.SecurityId, out var profile))
            {
                result.DevelopingPoc = profile.DevelopingTpoLevels.PointOfControl;
                result.DevelopingVah = profile.DevelopingTpoLevels.ValueAreaHigh;
                result.DevelopingVal = profile.DevelopingTpoLevels.ValueAreaLow;
                result.DevelopingVpoc = profile.DevelopingVolumeProfile.VolumePoc;
                result.InitialBalanceHigh = profile.InitialBalanceHigh;
                result.InitialBalanceLow = profile.InitialBalanceLow;
                result.InitialBalanceSignal = GetInitialBalanceSignal(instrument.LTP, profile, instrument.SecurityId);
                var historicalProfiles = _historicalMarketProfiles.GetValueOrDefault(instrument.SecurityId);
                result.MarketProfileSignal = GetMarketProfileSignal(instrument.LTP, profile, historicalProfiles, instrument);
                _marketProfileService.UpdateProfile(instrument.SecurityId, profile.ToMarketProfileData());
            }

            result.Symbol = instrument.DisplayName;
            result.Vwap = dayVwap;
            result.CurrentIv = instrument.ImpliedVolatility;
            result.AvgIv = avgIv;
            result.IvSignal = ivSignal;
            result.CurrentVolume = currentCandleVolume;
            result.AvgVolume = avgCandleVolume;
            result.VolumeSignal = volumeSignal;
            result.OiSignal = oiSignal;
            result.CustomLevelSignal = customLevelSignal;
            result.CandleSignal1Min = candleSignal1Min;
            result.CandleSignal5Min = candleSignal5Min;
            result.EmaSignal1Min = priceEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(1), "N/A");
            result.EmaSignal5Min = priceEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(5), "N/A");
            result.EmaSignal15Min = priceEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(15), "N/A");
            result.VwapEmaSignal1Min = vwapEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(1), "N/A");
            result.VwapEmaSignal5Min = vwapEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(5), "N/A");
            result.VwapEmaSignal15Min = vwapEmaSignals.GetValueOrDefault(TimeSpan.FromMinutes(15), "N/A");
            result.InstrumentGroup = GetInstrumentGroup(instrument);
            result.UnderlyingGroup = instrument.UnderlyingSymbol;
            result.PriceVsVwapSignal = paSignals.priceVsVwap;
            result.PriceVsCloseSignal = paSignals.priceVsClose;
            result.DayRangeSignal = paSignals.dayRange;
            result.OpenDriveSignal = paSignals.openDrive;

            SynthesizeTradeSignal(result);

            OnAnalysisUpdated?.Invoke(result);
        }

        // --- MODIFIED: This is the new, more intelligent signal synthesis logic ---
        private void SynthesizeTradeSignal(AnalysisResult result)
        {
            var bullishDrivers = new List<string>();
            var bearishDrivers = new List<string>();

            // --- Tier 1: Market Structure and Daily Bias (Highest Importance) ---
            if (result.DailyBias == "Strong Bullish") bullishDrivers.Add("Opening strong above previous day's value area.");
            if (result.DailyBias == "Strong Bearish") bearishDrivers.Add("Opening weak below previous day's value area.");
            if (result.MarketStructure == "Trending Up") bullishDrivers.Add("Multi-day structure is trending up.");
            if (result.MarketStructure == "Trending Down") bearishDrivers.Add("Multi-day structure is trending down.");

            // --- Tier 2: Intraday Market Profile and Price Action ---
            if (result.MarketProfileSignal == "Acceptance > Y-VAH") bullishDrivers.Add("Price accepted above yesterday's value.");
            if (result.MarketProfileSignal == "Acceptance < Y-VAL") bearishDrivers.Add("Price accepted below yesterday's value.");
            if (result.InitialBalanceSignal == "IB Extension Up") bullishDrivers.Add("Breaking out and extending above Initial Balance.");
            if (result.InitialBalanceSignal == "IB Extension Down") bearishDrivers.Add("Breaking down and extending below Initial Balance.");
            if (result.PriceVsVwapSignal == "Above VWAP") bullishDrivers.Add("Price is trading above VWAP.");
            if (result.PriceVsVwapSignal == "Below VWAP") bearishDrivers.Add("Price is trading below VWAP.");
            if (result.DayRangeSignal == "Near High") bullishDrivers.Add("Price is near the day's high.");
            if (result.DayRangeSignal == "Near Low") bearishDrivers.Add("Price is near the day's low.");

            // --- Tier 3: Momentum and Confirmation Indicators (Higher Timeframe Focus) ---
            if (result.EmaSignal5Min == "Bullish Cross" && result.EmaSignal15Min == "Bullish Cross") bullishDrivers.Add("5m & 15m EMAs in a bullish cross.");
            if (result.EmaSignal5Min == "Bearish Cross" && result.EmaSignal15Min == "Bearish Cross") bearishDrivers.Add("5m & 15m EMAs in a bearish cross.");
            if (result.OiSignal == "Long Buildup") bullishDrivers.Add("Price and Open Interest rising together (Long Buildup).");
            if (result.OiSignal == "Short Buildup") bearishDrivers.Add("Price falling while Open Interest rises (Short Buildup).");
            if (result.VolumeSignal == "Volume Burst" && result.PriceVsCloseSignal == "Above Close") bullishDrivers.Add("Volume spike on a positive candle.");
            if (result.VolumeSignal == "Volume Burst" && result.PriceVsCloseSignal == "Below Close") bearishDrivers.Add("Volume spike on a negative candle.");
            if (result.RsiSignal5Min == "Bullish Divergence") bullishDrivers.Add("5m Bullish RSI Divergence detected.");
            if (result.RsiSignal5Min == "Bearish Divergence") bearishDrivers.Add("5m Bearish RSI Divergence detected.");
            if (result.CandleSignal5Min.Contains("Bullish")) bullishDrivers.Add($"5m {result.CandleSignal5Min} pattern formed.");
            if (result.CandleSignal5Min.Contains("Bearish")) bearishDrivers.Add($"5m {result.CandleSignal5Min} pattern formed.");

            // --- Final Synthesis ---
            result.KeySignalDrivers = bullishDrivers.Concat(bearishDrivers).ToList();
            result.ConvictionScore = bullishDrivers.Count - bearishDrivers.Count;

            bool hasHighConvictionBullish = bullishDrivers.Count >= 2 && (result.DailyBias.Contains("Bullish") || result.MarketProfileSignal.Contains("Acceptance"));
            bool hasHighConvictionBearish = bearishDrivers.Count >= 2 && (result.DailyBias.Contains("Bearish") || result.MarketProfileSignal.Contains("Acceptance"));

            if (hasHighConvictionBullish && result.ConvictionScore >= 3)
            {
                result.FinalTradeSignal = "Strong Buy (Calls)";
            }
            else if (bullishDrivers.Count > bearishDrivers.Count && bullishDrivers.Count >= 2)
            {
                result.FinalTradeSignal = "Consider Calls";
            }
            else if (hasHighConvictionBearish && result.ConvictionScore <= -3)
            {
                result.FinalTradeSignal = "Strong Sell (Puts)";
            }
            else if (bearishDrivers.Count > bullishDrivers.Count && bearishDrivers.Count >= 2)
            {
                result.FinalTradeSignal = "Consider Puts";
            }
            else
            {
                result.FinalTradeSignal = "Neutral / No Edge";
            }
        }


        private string CalculateEmaSignal(string securityId, List<Candle> candles, Dictionary<string, Dictionary<TimeSpan, EmaState>> stateDictionary, bool useVwap)
        {
            if (candles.Count < LongEmaLength) return "Building History...";

            var timeframe = candles.Count > 1 ? (candles[1].Timestamp - candles[0].Timestamp) : TimeSpan.FromMinutes(1);
            var state = stateDictionary[securityId][timeframe];

            Func<Candle, decimal> sourceSelector = useVwap ? (c => c.Vwap) : (c => c.Close);

            var prices = candles.Select(sourceSelector).ToList();
            if (prices.Count == 0) return "Building History...";

            if (state.CurrentShortEma == 0 || state.CurrentLongEma == 0)
            {
                state.CurrentShortEma = prices.Skip(prices.Count - ShortEmaLength).Average();
                state.CurrentLongEma = prices.Average();
            }
            else
            {
                decimal shortMultiplier = 2.0m / (ShortEmaLength + 1);
                state.CurrentShortEma = ((prices.Last() - state.CurrentShortEma) * shortMultiplier) + state.CurrentShortEma;

                decimal longMultiplier = 2.0m / (LongEmaLength + 1);
                state.CurrentLongEma = ((prices.Last() - state.CurrentLongEma) * longMultiplier) + state.CurrentLongEma;
            }

            if (state.CurrentShortEma > state.CurrentLongEma) return "Bullish Cross";
            if (state.CurrentShortEma < state.CurrentLongEma) return "Bearish Cross";
            return "Neutral";
        }


        #region Helper Calculation Methods
        private (decimal avgIv, string ivSignal) CalculateIvSignal(decimal currentIv, List<decimal> ivHistory)
        {
            string signal = "Neutral";
            decimal avgIv = 0;
            var validIvHistory = ivHistory.Where(iv => iv > 0).ToList();

            if (validIvHistory.Any() && validIvHistory.Count >= MinIvHistoryForSignal)
            {
                avgIv = validIvHistory.Average();
                if (currentIv > (avgIv + this.IvSpikeThreshold)) signal = "IV Spike Up";
                else if (currentIv < (avgIv - this.IvSpikeThreshold)) signal = "IV Drop Down";
            }
            else if (currentIv > 0)
            {
                signal = "Building History...";
            }
            return (avgIv, signal);
        }

        private (string signal, long currentVolume, long averageVolume) CalculateVolumeSignalForTimeframe(List<Candle> candles)
        {
            if (!candles.Any()) return ("N/A", 0, 0);

            long currentCandleVolume = candles.Last().Volume;
            if (candles.Count < 2) return ("Building History...", currentCandleVolume, 0);

            var historyCandles = candles.Take(candles.Count - 1).ToList();
            if (historyCandles.Count > this.VolumeHistoryLength)
            {
                historyCandles = historyCandles.Skip(historyCandles.Count - this.VolumeHistoryLength).ToList();
            }

            if (!historyCandles.Any()) return ("Building History...", currentCandleVolume, 0);

            double averageVolume = historyCandles.Average(c => (double)c.Volume);
            if (averageVolume > 0 && currentCandleVolume > (averageVolume * this.VolumeBurstMultiplier))
            {
                return ("Volume Burst", currentCandleVolume, (long)averageVolume);
            }
            return ("Neutral", currentCandleVolume, (long)averageVolume);
        }

        private string CalculateOiSignal(List<Candle> candles)
        {
            if (candles.Count < 2) return "Building History...";

            var currentCandle = candles.Last();
            var previousCandle = candles[candles.Count - 2];

            if (previousCandle.OpenInterest == 0 || currentCandle.OpenInterest == 0)
            {
                return "Building History...";
            }

            bool isPriceUp = currentCandle.Close > previousCandle.Close;
            bool isPriceDown = currentCandle.Close < previousCandle.Close;
            bool isOiUp = currentCandle.OpenInterest > previousCandle.OpenInterest;
            bool isOiDown = currentCandle.OpenInterest < previousCandle.OpenInterest;

            if (isPriceUp && isOiUp) return "Long Buildup";
            if (isPriceUp && isOiDown) return "Short Covering";
            if (isPriceDown && isOiUp) return "Short Buildup";
            if (isPriceDown && isOiDown) return "Long Unwinding";

            return "Neutral";
        }

        private decimal CalculateRsi(List<Candle> candles, RsiState state, int period)
        {
            if (candles.Count <= period) return 0m;

            var lastCandle = candles.Last();
            var secondLastCandle = candles[candles.Count - 2];
            var change = lastCandle.Close - secondLastCandle.Close;
            var gain = Math.Max(0, change);
            var loss = Math.Max(0, -change);

            if (state.AvgGain == 0)
            {
                var initialChanges = candles.Skip(1).Select((c, i) => c.Close - candles[i].Close).ToList();
                state.AvgGain = initialChanges.Take(period).Where(ch => ch > 0).DefaultIfEmpty(0).Average();
                state.AvgLoss = initialChanges.Take(period).Where(ch => ch < 0).Select(ch => -ch).DefaultIfEmpty(0).Average();
            }
            else
            {
                state.AvgGain = ((state.AvgGain * (period - 1)) + gain) / period;
                state.AvgLoss = ((state.AvgLoss * (period - 1)) + loss) / period;
            }

            if (state.AvgLoss == 0) return 100m;

            var rs = state.AvgGain / state.AvgLoss;
            var rsi = 100 - (100 / (1 + rs));

            state.RsiValues.Add(rsi);
            if (state.RsiValues.Count > 50) state.RsiValues.RemoveAt(0);

            return Math.Round(rsi, 2);
        }

        private string DetectRsiDivergence(List<Candle> candles, RsiState state, int lookback)
        {
            if (candles.Count < lookback || state.RsiValues.Count < lookback) return "N/A";

            var relevantCandles = candles.TakeLast(lookback).ToList();
            var relevantRsi = state.RsiValues.TakeLast(lookback).ToList();
            int swingWindow = 3;

            var swingHighs = FindSwingPoints(relevantCandles, relevantRsi, isHigh: true, swingWindow);
            if (swingHighs.Count >= 2)
            {
                var high1 = swingHighs[0];
                var high2 = swingHighs[1];
                if (high1.price > high2.price && high1.indicator < high2.indicator)
                {
                    return "Bearish Divergence";
                }
            }

            var swingLows = FindSwingPoints(relevantCandles, relevantRsi, isHigh: false, swingWindow);
            if (swingLows.Count >= 2)
            {
                var low1 = swingLows[0];
                var low2 = swingLows[1];
                if (low1.price < low2.price && low1.indicator > low2.indicator)
                {
                    return "Bullish Divergence";
                }
            }

            return "Neutral";
        }

        private decimal CalculateObv(List<Candle> candles, ObvState state)
        {
            if (candles.Count < 2) return 0m;

            var lastCandle = candles.Last();
            var secondLastCandle = candles[candles.Count - 2];

            if (lastCandle.Close > secondLastCandle.Close)
            {
                state.CurrentObv += lastCandle.Volume;
            }
            else if (lastCandle.Close < secondLastCandle.Close)
            {
                state.CurrentObv -= lastCandle.Volume;
            }

            state.ObvValues.Add(state.CurrentObv);
            if (state.ObvValues.Count > 50) state.ObvValues.RemoveAt(0);

            return state.CurrentObv;
        }

        private string CalculateObvSignal(List<Candle> candles, ObvState state, int period)
        {
            if (state.ObvValues.Count < period) return "Building History...";

            var currentObv = state.CurrentObv;
            var previousObv = state.ObvValues.Count > 1 ? state.ObvValues[^2] : 0;

            var sma = state.ObvValues.TakeLast(period).Average();
            var previousSma = state.ObvValues.SkipLast(1).TakeLast(period).Average();
            state.CurrentMovingAverage = sma;

            bool wasBelow = previousObv < previousSma;
            bool isAbove = currentObv > sma;
            if (isAbove && wasBelow) return "Bullish Cross";

            bool wasAbove = previousObv > previousSma;
            bool isBelow = currentObv < sma;
            if (isBelow && wasAbove) return "Bearish Cross";

            if (isAbove) return "Trending Up";
            if (isBelow) return "Trending Down";

            return "Neutral";
        }

        private string DetectObvDivergence(List<Candle> candles, ObvState state, int lookback)
        {
            if (candles.Count < lookback || state.ObvValues.Count < lookback) return "N/A";

            var relevantCandles = candles.TakeLast(lookback).ToList();
            var relevantObv = state.ObvValues.TakeLast(lookback).ToList();
            int swingWindow = 3;

            var swingHighs = FindSwingPoints(relevantCandles, relevantObv, isHigh: true, swingWindow);
            if (swingHighs.Count >= 2)
            {
                var high1 = swingHighs[0];
                var high2 = swingHighs[1];
                if (high1.price > high2.price && high1.indicator < high2.indicator)
                {
                    return "Bearish Divergence";
                }
            }

            var swingLows = FindSwingPoints(relevantCandles, relevantObv, isHigh: false, swingWindow);
            if (swingLows.Count >= 2)
            {
                var low1 = swingLows[0];
                var low2 = swingLows[1];
                if (low1.price < low2.price && low1.indicator > low2.indicator)
                {
                    return "Bullish Divergence";
                }
            }

            return "Neutral";
        }


        private List<(decimal price, decimal indicator)> FindSwingPoints(List<Candle> candles, List<decimal> indicatorValues, bool isHigh, int window)
        {
            var swingPoints = new List<(decimal price, decimal indicator)>();
            for (int i = window; i < candles.Count - window; i++)
            {
                var currentPrice = isHigh ? candles[i].High : candles[i].Low;
                bool isSwing = true;
                for (int j = 1; j <= window; j++)
                {
                    var prevPrice = isHigh ? candles[i - j].High : candles[i - j].Low;
                    var nextPrice = isHigh ? candles[i + j].High : candles[i + j].Low;
                    if ((isHigh && (currentPrice < prevPrice || currentPrice < nextPrice)) ||
                        (!isHigh && (currentPrice > prevPrice || currentPrice > nextPrice)))
                    {
                        isSwing = false;
                        break;
                    }
                }
                if (isSwing)
                {
                    swingPoints.Add((currentPrice, indicatorValues[i]));
                }
            }
            return swingPoints.TakeLast(2).ToList();
        }

        private decimal CalculateAtr(List<Candle> candles, AtrState state, int period)
        {
            if (candles.Count < period) return 0m;

            var trueRanges = new List<decimal>();
            for (int i = 1; i < candles.Count; i++)
            {
                var high = candles[i].High;
                var low = candles[i].Low;
                var prevClose = candles[i - 1].Close;

                var tr = Math.Max(high - low, Math.Abs(high - prevClose));
                tr = Math.Max(tr, Math.Abs(low - prevClose));
                trueRanges.Add(tr);
            }

            if (!trueRanges.Any()) return 0m;

            if (state.CurrentAtr == 0)
            {
                state.CurrentAtr = trueRanges.Take(period).Average();
            }
            else
            {
                var lastTr = trueRanges.Last();
                state.CurrentAtr = ((state.CurrentAtr * (period - 1)) + lastTr) / period;
            }

            state.AtrValues.Add(state.CurrentAtr);
            if (state.AtrValues.Count > 20) state.AtrValues.RemoveAt(0);

            return Math.Round(state.CurrentAtr, 2);
        }

        private string GetAtrSignal(decimal currentAtr, AtrState state, int smaPeriod)
        {
            if (state.AtrValues.Count < smaPeriod) return "N/A";

            var smaOfAtr = state.AtrValues.TakeLast(smaPeriod).Average();
            var previousAtr = state.AtrValues.Count > 1 ? state.AtrValues[^2] : 0;
            var previousSmaOfAtr = state.AtrValues.Count > smaPeriod ? state.AtrValues.SkipLast(1).TakeLast(smaPeriod).Average() : 0;

            bool wasBelow = previousAtr < previousSmaOfAtr;
            bool isAbove = currentAtr > smaOfAtr;

            if (isAbove && wasBelow)
            {
                return "Vol Expanding";
            }

            bool wasAbove = previousAtr > previousSmaOfAtr;
            bool isBelow = currentAtr < smaOfAtr;

            if (isBelow && wasAbove)
            {
                return "Vol Contracting";
            }

            return isAbove ? "High Vol" : "Low Vol";
        }


        private (string priceVsVwap, string priceVsClose, string dayRange, string openDrive) CalculatePriceActionSignals(DashboardInstrument instrument, decimal vwap)
        {
            string priceVsVwap = "Neutral";
            if (vwap > 0)
            {
                if (instrument.LTP > vwap) priceVsVwap = "Above VWAP";
                else if (instrument.LTP < vwap) priceVsVwap = "Below VWAP";
            }

            string priceVsClose = "Neutral";
            if (instrument.Close > 0)
            {
                if (instrument.LTP > instrument.Close) priceVsClose = "Above Close";
                else if (instrument.LTP < instrument.Close) priceVsClose = "Below Close";
            }

            string dayRange = "Neutral";
            decimal range = instrument.High - instrument.Low;
            if (range > 0)
            {
                decimal positionInDayRange = (instrument.LTP - instrument.Low) / range;
                if (positionInDayRange > 0.8m) dayRange = "Near High";
                else if (positionInDayRange < 0.2m) dayRange = "Near Low";
                else dayRange = "Mid-Range";
            }

            string openDrive = "No";
            if (instrument.Open > 0 && instrument.Low > 0 && instrument.High > 0)
            {
                if (instrument.Open == instrument.Low) openDrive = "Drive Up";
                else if (instrument.Open == instrument.High) openDrive = "Drive Down";
            }

            return (priceVsVwap, priceVsClose, dayRange, openDrive);
        }

        private string CalculateCustomLevelSignal(DashboardInstrument instrument)
        {
            if (instrument.SegmentId != 0) return "N/A";

            var levels = _settingsViewModel.GetLevelsForIndex(instrument.Symbol);
            if (levels == null) return "No Levels Set";

            if (!_customLevelStates.ContainsKey(instrument.Symbol))
            {
                _customLevelStates[instrument.Symbol] = new IntradayIvState.CustomLevelState();
            }
            var state = _customLevelStates[instrument.Symbol];

            decimal ltp = instrument.LTP;
            IntradayIvState.PriceZone currentZone;

            if (ltp > levels.NoTradeUpperBand) currentZone = IntradayIvState.PriceZone.Above;
            else if (ltp < levels.NoTradeLowerBand) currentZone = IntradayIvState.PriceZone.Below;
            else currentZone = IntradayIvState.PriceZone.Inside;

            if (currentZone != state.LastZone)
            {
                if (state.LastZone == IntradayIvState.PriceZone.Inside && currentZone == IntradayIvState.PriceZone.Above) state.BreakoutCount++;
                else if (state.LastZone == IntradayIvState.PriceZone.Inside && currentZone == IntradayIvState.PriceZone.Below) state.BreakdownCount++;
                state.LastZone = currentZone;
            }

            switch (currentZone)
            {
                case IntradayIvState.PriceZone.Inside: return "No trade zone";
                case IntradayIvState.PriceZone.Above: return $"{GetOrdinal(state.BreakoutCount)} Breakout";
                case IntradayIvState.PriceZone.Below: return $"{GetOrdinal(state.BreakdownCount)} Breakdown";
                default: return "N/A";
            }
        }

        private string RecognizeCandlestickPattern(List<Candle> candles)
        {
            if (candles.Count < 1) return "N/A";

            string volInfo = candles.Count > 1 ? GetVolumeConfirmation(candles.Last(), candles[candles.Count - 2]) : "";

            if (candles.Count >= 3)
            {
                var c1 = candles.Last();
                var c2 = candles[candles.Count - 2];
                var c3 = candles[candles.Count - 3];

                bool isMorningStar = c3.Close < c3.Open && Math.Max(c2.Open, c2.Close) < c3.Close && c1.Close > c1.Open && c1.Close > (c3.Open + c3.Close) / 2;
                if (isMorningStar) return $"Bullish Morning Star{volInfo}";

                bool isEveningStar = c3.Close > c3.Open && Math.Min(c2.Open, c2.Close) > c3.Close && c1.Close < c1.Open && c1.Close < (c3.Open + c3.Close) / 2;
                if (isEveningStar) return $"Bearish Evening Star{volInfo}";

                bool areThreeWhiteSoldiers = c3.Close > c3.Open && c2.Close > c2.Open && c1.Close > c1.Open && c2.Open > c3.Open && c2.Close > c3.Close && c1.Open > c2.Open && c1.Close > c2.Close;
                if (areThreeWhiteSoldiers) return "Bullish Three Soldiers";

                bool areThreeBlackCrows = c3.Close < c3.Open && c2.Close < c2.Open && c1.Close < c1.Open && c2.Open < c3.Open && c2.Close < c3.Close && c1.Open < c2.Open && c1.Close < c2.Close;
                if (areThreeBlackCrows) return "Bearish Three Crows";
            }

            if (candles.Count >= 2)
            {
                var c1 = candles.Last();
                var c2 = candles[candles.Count - 2];

                if (c1.Close > c1.Open && c2.Close < c2.Open && c1.Close > c2.Open && c1.Open < c2.Close) return $"Bullish Engulfing{volInfo}";
                if (c1.Close < c1.Open && c2.Close > c2.Open && c1.Open > c2.Close && c1.Close < c2.Open) return $"Bearish Engulfing{volInfo}";
                if (c2.Close < c2.Open && c1.Close > c1.Open && Math.Abs(c1.Low - c2.Low) < (c1.High - c1.Low) * 0.05m) return "Bullish Tweezer";
                if (c2.Close > c2.Open && c1.Close < c1.Open && Math.Abs(c1.High - c2.High) < (c1.High - c1.Low) * 0.05m) return "Bearish Tweezer";

                decimal c1Body = Math.Abs(c1.Close - c1.Open);
                decimal c2Body = Math.Abs(c2.Close - c2.Open);
                if (c2Body > c1Body * 3 && Math.Max(c1.Close, c1.Open) < Math.Max(c2.Close, c2.Open) && Math.Min(c1.Close, c1.Open) > Math.Min(c2.Close, c2.Open))
                {
                    if (c1Body / (c1.High - c1.Low + 0.0001m) < 0.1m) return "Neutral Harami Cross";
                    return c2.Close > c2.Open ? "Bearish Harami" : "Bullish Harami";
                }
            }

            var current = candles.Last();
            decimal body = Math.Abs(current.Open - current.Close);
            decimal range = current.High - current.Low;
            if (range == 0) return "Neutral";

            decimal upperShadow = current.High - Math.Max(current.Open, current.Close);
            decimal lowerShadow = Math.Min(current.Open, current.Close) - current.Low;

            if (body / range < 0.1m) return "Neutral Doji";
            if (lowerShadow > body * 2 && upperShadow < body) return current.Close > current.Open ? "Bullish Hammer" : "Bearish Hanging Man";
            if (upperShadow > body * 2 && lowerShadow < body) return current.Close > current.Open ? "Bullish Inv Hammer" : "Bearish Shooting Star";
            if (body / range > 0.95m)
            {
                if (current.Close > current.Open) return $"Bullish Marubozu{volInfo}";
                if (current.Close < current.Open) return $"Bearish Marubozu{volInfo}";
            }

            return "N/A";
        }


        private string GetVolumeConfirmation(Candle current, Candle previous)
        {
            if (previous.Volume > 0)
            {
                decimal volChange = ((decimal)current.Volume - previous.Volume) / previous.Volume;
                if (volChange > 0.2m)
                {
                    return $" (+{volChange:P0} Vol)";
                }
            }
            return "";
        }

        private string GetOrdinal(int num)
        {
            if (num <= 0) return num.ToString();
            switch (num % 100)
            {
                case 11: case 12: case 13: return num + "th";
            }
            switch (num % 10)
            {
                case 1: return num + "st";
                case 2: return num + "nd";
                case 3: return num + "rd";
                default: return num + "th";
            }
        }

        private string GetInstrumentGroup(DashboardInstrument instrument)
        {
            if (instrument.SegmentId == 0) return "Indices";
            if (instrument.IsFuture) return "Futures";
            if (instrument.DisplayName.ToUpper().Contains("CALL") || instrument.DisplayName.ToUpper().Contains("PUT")) return "Options";
            return "Stocks";
        }

        #endregion

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }
}
