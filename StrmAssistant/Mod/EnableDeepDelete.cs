using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnableDeepDelete : PatchBase<EnableDeepDelete>
    {
        private static MethodInfo _deleteItem;

        public EnableDeepDelete()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().EnableDeepDelete)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var libraryManager =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Library.LibraryManager");
            _deleteItem = libraryManager.GetMethod("DeleteItem",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(BaseItem), typeof(bool) }, null);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _deleteItem, prefix: nameof(DeleteItemPrefix),
                finalizer: nameof(DeleteItemFinalizer));
        }

        [HarmonyPrefix]
        private static void DeleteItemPrefix(ILibraryManager __instance, BaseItem item, DeleteOptions options,
            BaseItem parent, bool notifyParentItem, out Dictionary<string, bool?> __state)
        {
            __state = null;

            if (options.DeleteFileLocation)
            {
                var collectionFolder = options.CollectionFolders ?? __instance.GetCollectionFolders(item);
                var scope = item.GetDeletePaths(true, collectionFolder).Select(i => i.FullName).ToArray();

                __state = Plugin.LibraryApi.PrepareDeepDelete(item, scope);
            }
        }

        [HarmonyFinalizer]
        private static void DeleteItemFinalizer(Exception __exception, Dictionary<string, bool?> __state)
        {
            if (__state != null && __state.Count > 0 && __exception is null)
            {
                var localMountPaths = new HashSet<string>(__state.Where(kv => kv.Value is true).Select(kv => kv.Key));

                if (localMountPaths.Count > 0)
                {
                    Task.Run(() => Plugin.LibraryApi.ExecuteDeepDelete(localMountPaths)).ConfigureAwait(false);
                }
            }
        }
    }
}
