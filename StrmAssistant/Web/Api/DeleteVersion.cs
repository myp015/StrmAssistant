using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Items/{Id}/DeleteVersion", "POST")]
    [Authenticated]
    public class DeleteVersion : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "DeleteParent", Description = "Delete Parent", IsRequired = false, DataType = "string",
            ParameterType = "query")]
        public bool DeleteParent { get; set; }
    }
}
