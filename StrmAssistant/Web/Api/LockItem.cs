using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Items/{ItemId}/Lock", "POST")]
    [Authenticated(Roles = "Admin")]
    public class LockItem : IReturnVoid, IReturn
    {
        [ApiMember(Name = "ItemId", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string ItemId { get; set; }

        [ApiMember(Name = "LockData", Description = "Indicates if lock or unlock metadata.", IsRequired = true,
            DataType = "bool", ParameterType = "query")]
        public bool LockData { get; set; }
    }
}
