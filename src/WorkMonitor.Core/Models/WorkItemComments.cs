namespace WorkMonitor.Core.Models
{
    public class WorkItemComments
    {
        public int WorkItemId { get; set; }
        public CustomFields CustomFields { get; set; }
        public List<Comment>? Comments { get; set; }
        public List<CustomerComment>? CustomerComments { get; set; }

    }
}
