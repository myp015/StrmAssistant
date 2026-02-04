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

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                PresentationUniqueKey = itemById.PresentationUniqueKey
            });

            foreach (var item in items)
            {
                if (item.IsLocked != request.LockData)
                {
                    item.IsLocked = request.LockData;
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                }

                if (item is Folder folder)
                {
                    foreach (var child in folder.GetItemList(new InternalItemsQuery { Recursive = true }))
                    {
                        if (child.IsLocked != request.LockData)
                        {
                            child.IsLocked = request.LockData;
                            child.UpdateToRepository(ItemUpdateType.MetadataEdit);
                        }
                    }
                }
            }
        }
    }
}
