using System;

namespace CloudSync.WebDav
{
    internal class WebDavResource
    {
        public string Href { get; set; } = "";
        public DateTime LastModified { get; set; }
        public long ContentLength { get; set; }
        public bool IsCollection { get; set; }
    }
}
