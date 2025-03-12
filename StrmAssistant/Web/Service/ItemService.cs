using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using StrmAssistant.Web.Api;

namespace StrmAssistant.Web.Service
{
    public class ItemService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public ItemService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Post(LockItem request)
        {
            var itemById = _libraryManager.GetItemById(request.ItemId);

            var lockStateChanged = itemById.IsLocked != request.LockData;
            if (!lockStateChanged) return;

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                PresentationUniqueKey = itemById.PresentationUniqueKey
            });

            foreach (var item in items)
            {
                item.IsLocked = request.LockData;
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);

                if (item is Folder folder)
                {
                    foreach (var child in folder.GetItemList(new InternalItemsQuery { Recursive = true }))
                    {
                        child.IsLocked = request.LockData;
                        child.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    }
                }
            }
        }
    }
}
