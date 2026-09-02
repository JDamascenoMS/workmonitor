namespace WorkMonitor.Core.Models
{
    public class VsDivCommentResponse
    {
        public int id {  get; set; }
        public string title {  get; set; }
        public string content { get; set; }

        public string text { get; set; }
        public List<CustomerComment> partialHistory { get; set; }
        public List<CustomerComment> children { get; set; }

    }

    public class CustomerComment
    {
        public int id { get; set; }
        public int parentId { get; set; }
        public int postId { get; set; }
        public Author author { get; set; }
        public int action { get; set; }
        public string description { get; set; }
        public string text { get; set; }
        public string url { get; set; }
    }

    public class Author
    {
        public int id { get; set; }
        public string name { get; set; }
    }
}
