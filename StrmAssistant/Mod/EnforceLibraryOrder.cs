using HarmonyLib;
using MediaBrowser.Controller.Entities;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnforceLibraryOrder : PatchBase<EnforceLibraryOrder>
    {
        private static MethodInfo _getUserViews;

        public EnforceLibraryOrder()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().UIFunctionOptions.EnforceLibraryOrder)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var userViewManager =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Library.UserViewManager");
            _getUserViews = userViewManager.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "GetUserViews" &&
                                     (m.GetParameters().Length == 3 || m.GetParameters().Length == 4));
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getUserViews, postfix: nameof(GetUserViewsPostfix));
        }

        [HarmonyPostfix]
        private static void GetUserViewsPostfix(ref Folder[] __result)
        {
            try
            {
                if (__result == null || __result.Length == 0 || LibraryApi.AdminOrderedViews == null ||
                    LibraryApi.AdminOrderedViews.Length == 0)
                {
                    return;
                }

                // 根据 AdminOrderedViews 对 Folder 数组进行排序
                var orderedViews = LibraryApi.AdminOrderedViews;
                var sortedList = new List<Folder>();
                var remainingFolders = __result.ToList();

                // 先按照 AdminOrderedViews 的顺序添加
                foreach (var orderedViewId in orderedViews)
                {
                    var folder = remainingFolders.FirstOrDefault(f => f.Id.ToString("N") == orderedViewId);
                    if (folder != null)
                    {
                        sortedList.Add(folder);
                        remainingFolders.Remove(folder);
                    }
                }

                // 将剩余未排序的文件夹添加到末尾
                sortedList.AddRange(remainingFolders);

                __result = sortedList.ToArray();
            }
            catch (Exception ex)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"EnforceLibraryOrder GetUserViewsPostfix error: {ex.Message}");
                }
            }
        }
    }
}
