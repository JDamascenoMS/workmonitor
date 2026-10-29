namespace WorkMonitor.Core.Models
{
    public class WorkItemRequest
    {
        public List<int> ids {  get; set; }
        public List<string> fields { get; set; }

        public WorkItemRequest(List<int> ListIds) 
        {
            ids = ListIds;
            fields = new List<string>() { 
                "System.Id", "System.Title", "System.AreaPath", "System.CreatedDate",
                "System.Description", "System.State", "System.AssignedTo", "System.CreatedBy",
                "System.WorkItemType", "System.ChangedDate", "System.Tags",
                "Microsoft.VSTS.Common.Priority",
                "Microsoft.DevDiv.DeveloperCommunityLink", "Microsoft.DevDiv.DeveloperCommunityId"
            };
        }
    }
}
