using Newtonsoft.Json;

namespace FT_MONITOR.Model
{
    public class WorkItemResponse
    {
        public int Id { get; set; }
        public List<InternalItem> value { get; set; }
    }

    public class InternalItem
    {
        public int id {  get; set; }
        public int rev { get; set; }
        public CustomFields fields { get; set; }
        public string url { get; set; }
    }

    public class CustomFields
    {
        [JsonProperty(PropertyName = "System.Id")]
        public int Id { get; set; }

        [JsonProperty(PropertyName = "System.Title")]
        public string Title { get; set; }

        [JsonProperty(PropertyName = "System.AreaPath")]
        public string AreaPath { get; set; }

        [JsonProperty(PropertyName = "System.CreatedDate")]
        public string CreatedDate { get; set; }

        [JsonProperty(PropertyName = "System.Description")]
        public string Description { get; set; }

        [JsonProperty(PropertyName = "System.State")]
        public string State { get; set; }

        [JsonProperty(PropertyName = "System.WorkItemType")]
        public string WorkItemType { get; set; }

        [JsonProperty(PropertyName = "Microsoft.VSTS.Common.Priority")]
        public int Priority { get; set; }

        [JsonProperty(PropertyName = "System.AssignedTo")]
        public IdentityRef AssignedTo { get; set; }

        [JsonProperty(PropertyName = "System.CreatedBy")]
        public IdentityRef CreatedBy { get; set; }

        [JsonProperty(PropertyName = "Microsoft.DevDiv.DeveloperCommunityLink")]
        public string UrlPostId { get; set; }

        [JsonProperty(PropertyName = "Microsoft.DevDiv.DeveloperCommunityId")]
        public string PostId { get; set; }

        [JsonProperty(PropertyName = "System.ChangedDate")]
        public string ChangedDate { get; set; }

    }

    public class IdentityRef
    {
        public string displayName { get; set; }
        public string uniqueName { get; set; }
    }

}

            
