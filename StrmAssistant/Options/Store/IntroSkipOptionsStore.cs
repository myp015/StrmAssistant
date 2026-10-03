using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Options.Store
{
    public class IntroSkipOptionsStore : SimpleFileStore<IntroSkipOptions>
    {
        private readonly ILogger _logger;

        public IntroSkipOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            FileSaved += OnFileSaved;
            FileSaving += OnFileSaving;
        }

        public IntroSkipOptions IntroSkipOptions => GetOptions();

        // 过滤 scope 中不在可选列表内的值；可选列表为空(未初始化)时保留用户原值并告警，避免静默清空
        private string FilterScope(string rawScope, List<EditorSelectOption> allowedList, string scopeName)
        {
            var inputIds = rawScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToArray() ?? Array.Empty<string>();

            if (allowedList.Count == 0 && inputIds.Length > 0)
            {
                _logger.Warn("{0}: 可选项列表为空（可能未调用 Initialize），已保留用户原值: {1}", scopeName, rawScope);
                return rawScope;
            }

            var validValues = new HashSet<string>(allowedList.Select(o => o.Value));
            var kept = inputIds.Where(validValues.Contains).ToArray();
            var dropped = inputIds.Except(kept).ToArray();
            if (dropped.Length > 0)
                _logger.Warn("{0}: 过滤掉 {1} 个不在可选范围内的值: {2}", scopeName, dropped.Length, string.Join(", ", dropped));

            return string.Join(",", kept);
        }

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is IntroSkipOptions options)
            {
                options.LibraryScope = FilterScope(options.LibraryScope, options.LibraryList,
                    nameof(IntroSkipOptions.LibraryScope));

                options.UserScope = FilterScope(options.UserScope, options.UserList,
                    nameof(IntroSkipOptions.UserScope));

                var isModSupported = options.IsModSupported;
                options.MarkerEnabledLibraryScope = isModSupported
                    ? options.MarkerEnabledLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Contains("-1") == true
                        ? "-1"
                        : FilterScope(options.MarkerEnabledLibraryScope, options.MarkerEnabledLibraryList,
                            nameof(IntroSkipOptions.MarkerEnabledLibraryScope))
                    : string.Empty;

                if (isModSupported)
                {
                    var blacklistShowIds = options.FingerprintBlacklistShows
                        .Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(part => long.TryParse(part.Trim(), out var id) ? id : (long?)null)
                        .Where(id => id.HasValue)
                        .Select(id => id.Value)
                        .ToArray();

                    var items = Plugin.LibraryApi.GetItemsByIds(blacklistShowIds);

                    options.FingerprintBlacklistShowsResult.Clear();

                    foreach (var item in items.Where(item => item is Series || item is Season))
                    {
                        var listItem = new GenericListItem();

                        if (item is Series series)
                        {
                            listItem.PrimaryText = $"{series.Name} ({series.InternalId}) - {series.ContainingFolderPath}";
                        }
                        else if (item is Season season)
                        {
                            listItem.PrimaryText =
                                $"{season.SeriesName} - {season.Name} ({season.InternalId}) - {season.ContainingFolderPath}";
                        }

                        listItem.Icon = IconNames.block;
                        listItem.IconMode = ItemListIconMode.SmallRegular;

                        options.FingerprintBlacklistShowsResult.Add(listItem);
                    }
                }
                else
                {
                    options.FingerprintBlacklistShows = string.Empty;
                    options.FingerprintBlacklistShowsResult.Clear();
                }

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(IntroSkipOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (changedProperties.Contains(nameof(IntroSkipOptions.EnableIntroSkip)))
                {
                    if (options.EnableIntroSkip)
                    {
                        Plugin.PlaySessionMonitor.Initialize();
                    }
                    else
                    {
                        Plugin.PlaySessionMonitor.Dispose();
                    }
                }

                if (changedProperties.Contains(nameof(IntroSkipOptions.IntroSkipPreferences)))
                {
                    UpdateIntroSkipPreferences(options.IntroSkipPreferences);
                }

                if (changedProperties.Contains(nameof(IntroSkipOptions.LibraryScope)) ||
                    changedProperties.Contains(nameof(IntroSkipOptions.EnableIntroSkip)))
                {
                    if (options.EnableIntroSkip)
                        Plugin.PlaySessionMonitor.UpdateLibraryPathsInScope(options.LibraryScope);
                }

                if (changedProperties.Contains(nameof(IntroSkipOptions.UserScope)) ||
                    changedProperties.Contains(nameof(IntroSkipOptions.EnableIntroSkip)))
                {
                    if (options.EnableIntroSkip)
                        Plugin.PlaySessionMonitor.UpdateUsersInScope(options.UserScope);
                }

                if (changedProperties.Contains(nameof(IntroSkipOptions.ClientScope)) ||
                    changedProperties.Contains(nameof(IntroSkipOptions.EnableIntroSkip)))
                {
                    if (options.EnableIntroSkip)
                        Plugin.PlaySessionMonitor.UpdateClientInScope(options.ClientScope);
                }

                if (changedProperties.Contains(nameof(IntroSkipOptions.UnlockIntroSkip)))
                {
                    if (options.UnlockIntroSkip)
                    {
                        if (options.IsModSupported) PatchManager.UnlockIntroSkip.Patch();
                    }
                    else
                    {
                        if (options.IsModSupported) PatchManager.UnlockIntroSkip.Unpatch();
                        Plugin.FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength(10);
                    }
                }

                if (options.UnlockIntroSkip)
                {
                    Plugin.FingerprintApi.UpdateLibraryPathsInScope(options.MarkerEnabledLibraryScope);
                    Plugin.FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength(options.IntroDetectionFingerprintMinutes);
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is IntroSkipOptions options)
            {
                _logger.Info("UnlockIntroSkip is set to {0}", options.UnlockIntroSkip);
                _logger.Info("IntroDetectionFingerprintMinutes is set to {0}",
                    options.IntroDetectionFingerprintMinutes);

                var markerEnabledLibraryScope = string.Join(", ",
                    options.MarkerEnabledLibraryScope
                        ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v =>
                            options.MarkerEnabledLibraryList
                                .FirstOrDefault(option => option.Value == v)?.Name) ?? Enumerable.Empty<string>());
                _logger.Info("MarkerEnabledLibraryScope is set to {0}",
                    string.IsNullOrEmpty(markerEnabledLibraryScope)
                        ? options.MarkerEnabledLibraryList.Any(o => o.Value != "-1") ? "ALL" : "EMPTY"
                        : markerEnabledLibraryScope);

                _logger.Info("FingerprintBlacklistShows is set to {0}", options.FingerprintBlacklistShows);

                _logger.Info("EnableIntroSkip is set to {0}", options.EnableIntroSkip);
                _logger.Info("MaxIntroDurationSeconds is set to {0}", options.MaxIntroDurationSeconds);
                _logger.Info("MaxCreditsDurationSeconds is set to {0}", options.MaxCreditsDurationSeconds);
                _logger.Info("MinOpeningPlotDurationSeconds is set to {0}",
                    options.MinOpeningPlotDurationSeconds);

                var intoSkipLibraryScope = string.Join(", ",
                    options.LibraryScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.LibraryList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                _logger.Info("IntroSkip - LibraryScope is set to {0}",
                    string.IsNullOrEmpty(intoSkipLibraryScope) ? "ALL" : intoSkipLibraryScope);

                var introSkipUserScope = string.Join(", ",
                    options.UserScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => options.UserList
                            .FirstOrDefault(option => option.Value == v)
                            ?.Name) ?? Enumerable.Empty<string>());
                _logger.Info("IntroSkip - UserScope is set to {0}",
                    string.IsNullOrEmpty(introSkipUserScope) ? "ALL" : introSkipUserScope);

                _logger.Info("IntroSkip - ClientScope is set to {0}", options.ClientScope);

                var introSkipPreferences = GetSelectedIntroSkipPreferenceDescription();
                _logger.Info("IntroSkip - Preferences is set to {0}",
                    string.IsNullOrEmpty(introSkipPreferences) ? "EMPTY" : introSkipPreferences);

                // UnlockIntroSkip 已开启但声纹提取范围为空的可见提示
                if (options.UnlockIntroSkip && string.IsNullOrEmpty(options.MarkerEnabledLibraryScope))
                {
                    var hasMarkerEnabledLibs = options.MarkerEnabledLibraryList.Any(o => o.Value != "-1");
                    _logger.Warn(hasMarkerEnabledLibs
                        ? "UnlockIntroSkip 已开启但「声纹提取媒体库范围」为空：指纹提取将覆盖所有启用 marker 检测的剧集媒体库。若需限定范围，请在「声纹提取媒体库范围」中设置。"
                        : "UnlockIntroSkip 已开启但没有任何启用 marker 检测的剧集媒体库，指纹提取将无可用媒体库。请先在媒体库选项中开启片头检测。");
                }
            }
        }
    }
}
