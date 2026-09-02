namespace WorkMonitor.Core.Models
{
    public class WiqlResponse
    {
        public string queryType {  get; set; }
        public string asOf { get; set; }
        public List<Fields> columns { get; set; }
        public List<Sorts> sortColumns { get; set; }
        public List<WorkItem> workItems { get; set; }

    }

    public class Fields
    {
        public string referenceName { get; set; }
        public string name { get; set; }
        public string url { get; set; }
    }

    

    public class WorkItem 
    {
        public int id { get; set; }
        public string url { get; set; }
    }

    public class Sorts
    {
        public Fields field { get; set; }
        public bool descending { get; set; }

    }

}
