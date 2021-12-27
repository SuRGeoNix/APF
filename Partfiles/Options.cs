using System;
using System.IO;

namespace SuRGeoNix.Partfiles
{
    /// <summary>
    /// Advanced Partfiles Options
    /// </summary>
    public class Options : ICloneable
    {
        /// <summary>
        /// The folder that the completed file will be placed (Default: %current%)
        /// </summary>
        public string   Folder              { get; set; } = Environment.CurrentDirectory;
        /// <summary>
        /// The folder that the part file will be placed (Default: %temp%)
        /// </summary>
        public string   PartFolder          { get; set; } = Path.GetTempPath();
        /// <summary>
        /// The file extension of the part file (Default: .apf)
        /// </summary>
        public string   PartExtension       { get; set; } = ".apf";
        /// <summary>
        /// To overwrite the completed file if exists (Default: false)
        /// </summary>
        public bool     Overwrite           { get; set; } = false;
        /// <summary>
        /// To overwrite the part file if exists (Default: false)
        /// </summary>
        public bool     PartOverwrite       { get; set; } = false;
        /// <summary>
        /// To automatically create the completed file when and if the size has been specified and reached (Default: true)
        /// </summary>
        public bool     AutoCreate          { get; set; } = true;
        /// <summary>
        /// To delete the completed file on dispose (Default: false)
        /// </summary>
        public bool     DeleteOnDispose     { get; set; } = false;
        /// <summary>
        /// To delete the part file on dispose (Default: false)
        /// </summary>
        public bool     DeletePartOnDispose { get; set; } = false;
        /// <summary>
        /// To delete the part file after creating the completed file (Default: true)
        /// </summary>
        public bool     DeletePartOnCreate  { get; set; } = true;
        /// <summary>
        /// File stream will be changed from part to completed file after creation to continue support reading process (Default: true)
        /// </summary>
        public bool     StayAlive           { get; set; } = true;
        /// <summary>
        /// The size of first chunk if it is known (will allow position reading process to work without receiving it first)
        /// </summary>
        public int      FirstChunksize      { get; set; } = -1;
        /// <summary>
        /// The size of last chunk if it is known
        /// </summary>
        public int      LastChunksize       { get; set; } = -1;
        /// <summary>
        /// Whether to flush on every chunk write
        /// </summary>
        public bool     FlushOnEveryChunk   { get; set; } = false;

        /// <summary>
        /// Clones this options to a new instance
        /// </summary>
        /// <returns></returns>
        public object Clone() { return (Options) MemberwiseClone(); }
    }
}
