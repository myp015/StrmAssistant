using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Items/{Id}/ClearIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearIntro : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }
}
