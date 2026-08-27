namespace FT_MONITOR.Model
{
    public class CommentsResponse
    {
        public int totalCount {  get; set; }
        public int count { get; set; }
        public List<Comment> comments { get; set; }
        public string nextPage { get; set; }
        public string continuationToken { get; set; }


    }

    public class Comment
    {
        public int workItemId { get; set; }
        public int commentId { get; set; }
        public int version { get; set; }
        public string text { get; set; }
        public string modifiedDate { get; set; }
        public bool isDeleted { get; set; }
        public string url { get; set; }

    }
 
    

}
