using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class NoBoxsetsAutoCreation : PatchBase<NoBoxsetsAutoCreation>
    {
        private static MethodInfo _ensureLibraryFolder;
        private static MethodInfo _getUserViews;

        public NoBoxsetsAutoCreation()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().UIFunctionOptions.NoBoxsetsAutoCreation)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            
            // 1. 自动创建合集文件夹的方法
            var collectionManager = embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Collections.CollectionManager");
            _ensureLibraryFolder = collectionManager?.GetMethod("EnsureLibraryFolder", BindingFlags.Instance | BindingFlags.NonPublic);
            
            // 2. 用户视图查询方法（仅支持 4.9.1+ 的 3 参数版本）
            var userViewManager = embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Library.UserViewManager");
            _getUserViews = userViewManager?.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "GetUserViews" && m.GetParameters().Length == 3);

            if (_ensureLibraryFolder == null || _getUserViews == null)
            {
                Plugin.Instance.Logger.Warn("NoBoxsetsAutoCreation - Required methods for Emby 4.9.1+ not found");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _ensureLibraryFolder, prefix: nameof(EnsureLibraryFolderPrefix));
            PatchUnpatch(PatchTracker, apply, _getUserViews, prefix: nameof(GetUserViewsPrefix));
        }

        [HarmonyPrefix]
        private static bool EnsureLibraryFolderPrefix()
        {
            // 禁止 Emby 自动创建合集库文件夹
            return false;
        }

        /// <summary>
        /// 核心修复：移除 User 参数声明。
        /// 在 Harmony 中，如果不确定参数是否存在，不要将其写在方法签名里。
        /// 如果需要 User，可以通过 __args 或手动从 query 获取。
        /// </summary>
        [HarmonyPrefix]
        private static bool GetUserViewsPrefix(UserViewQuery query)
        {
            // 4.9 版本中这个方法不再通过参数传递 User
            // 我们保留此 Prefix 主要是为了拦截并修改 query（如果需要）
            // 或者配合 Postfix 过滤结果。
            return true;
        }

        /// <summary>
        /// 使用 Postfix 来过滤返回的 folders 列表，移除合集库。
        /// </summary>
        [HarmonyPostfix]
        private static void GetUserViewsPostfix(ref Folder[] __result)
        {
            if (__result == null) return;

            // 过滤掉 CollectionType 为 BoxSets 的文件夹
            __result = __result.Where(i => 
                !(i is CollectionFolder library) || 
                library.CollectionType != CollectionType.BoxSets.ToString()
            ).ToArray();
        }
    }
}