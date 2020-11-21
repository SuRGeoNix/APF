using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace SuRGeoNix.Partfiles
{
    /// <summary>
    /// Advanced Partfiles (thread-safe read/write)
    /// </summary>
    public class Partfile : IDisposable
    {
        public static Version Version   { get; private set; } = Assembly.GetExecutingAssembly().GetName().Version;

        public string   Filename        { get; private set; } = null;
        public int      Chunksize       { get; private set; } = -1;
        public long     Size            { get; private set; } = -1;
        public Options  Options         { get; private set; } = null;

        public bool     Created         { get; private set; } = false;
        public bool     Disposed        { get; private set; } = false;
        public long     Partsize        { get; private set; } = -1;
        public int      ChunksWritten   => curChunkPos + 1;
        public int      ChunksTotal     { get; private set; } = -1;
        public int      FirstChunkpos   { get; private set; } = -1;
        public int      LastChunkpos    { get; private set; } = -1;
        
        public Dictionary<int, int> MapChunkIdToChunkPos { get; private set; }

        public event FileCreatingHandler FileCreating;
        public delegate void FileCreatingHandler(Partfile partfile, EventArgs e);

        public event FileCreatedHandler FileCreated;
        public delegate void FileCreatedHandler(Partfile partfile, EventArgs e);

        public event WarningsHandler Warnings;
        public delegate void WarningsHandler(Partfile partfile, WarningEventArgs e);
        public class WarningEventArgs
        {
            public string Message { get; set; }
            public WarningEventArgs(string msg) { Message = msg; }
        }

        /// <summary>
        /// Prepeares a new partfile
        /// </summary>
        /// <param name="filename">Will be used for both part and completed files. It could also be a path eg. folder/file.ext</param>
        /// <param name="chunksize">The main chunksize. Check also Options for first/last chunksize</param>
        /// <param name="options"></param>
        public Partfile(string filename, int chunksize, Options options = null) : this(filename, chunksize, -1, options) { }
        /// <summary>
        /// Prepares a new partfile
        /// </summary>
        /// <param name="filename">Will be used for both part and completed files. It could also be a path eg. folder/file.ext</param>
        /// <param name="chunksize">The main chunksize. Check also Options for first/last chunksize</param>
        /// <param name="size"></param>
        /// <param name="options"></param>
        public Partfile(string filename, int chunksize, long size, Options options = null)
        {
            Filename    = filename;
            Chunksize   = chunksize;
            Size        = size;
            Options     = options == null ? new Options() : (Options) options.Clone();

            // Validation Sizes
            if (Chunksize < 1)                      ThrowException("Chunksize must be greater than zero");
            if (Size != -1 && Size < 1)             ThrowException("Size must be greater than zero");
            //if (Size != -1 && Chunksize > Size)     ThrowException("Size must be greater than chunksize");
            if (Size == -1 && Options.AutoCreate)   ThrowException("AutoCreate must be set to false for initially unknown size part files");

            if (Options.FirstChunksize != -1 && Options.FirstChunksize > Chunksize)
                ThrowException("First chunk size must be less or equal to chunksize");

            if (Options.LastChunksize  != -1 && Options.LastChunksize  > Chunksize)
                ThrowException("Last chunk size must be less or equal to chunksize");
            
            // Validation Overwrites
            if (File.Exists(Path.Combine(Options.Folder, Filename)))
            {
                if (!Options.Overwrite)             ThrowException($"Exists already in {Options.Folder}");
                File.Delete(Path.Combine(Options.Folder, Filename));
            }

            if (File.Exists(Path.Combine(Options.PartFolder, Filename + Options.PartExtension)))
            {
                if (!Options.PartOverwrite)         ThrowException($"Exists already in {Options.PartFolder}");
                File.Delete(Path.Combine(Options.PartFolder, Filename + Options.PartExtension));
            }

            // Create Folders
            Directory.CreateDirectory((new FileInfo(Path.Combine(Options.Folder,     Filename))).DirectoryName);
            Directory.CreateDirectory((new FileInfo(Path.Combine(Options.PartFolder, Filename))).DirectoryName);

            // Create & Open Partfile | Write headers
            try
            {
                fileStream = File.Open(Path.Combine(Options.PartFolder, Filename) + Options.PartExtension, FileMode.CreateNew, FileAccess.ReadWrite);
                WriteHeaders();
                CalculatePartsize();
            } catch (Exception e) { ThrowException(e.Message); }

            MapChunkIdToChunkPos = new Dictionary<int, int>();
        }
        /// <summary>
        /// Loads an existing partfile
        /// </summary>
        /// <param name="partfile">Absolute path of the existing part file</param>
        /// <param name="options">Warning: main options will be used from the saved part file</param>
        public Partfile(string partfile, Options options = null)
        {
            if (!File.Exists(partfile)) throw new Exception($"Partfile '{partfile}' does not exist");

            Options = options == null ? new Options() : (Options) options.Clone();

            fileStream = File.Open(partfile, FileMode.Open, FileAccess.ReadWrite);

            // Type
            byte[] readBuff = new byte[3];
            fileStream.Read(readBuff, 0, 3);

            if (Encoding.UTF8.GetString(readBuff) != "APF") throw new IOException($"Invalid headers in partfile");

            // Major
            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);

            // Minor
            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);

            readBuff = new byte[8];
            fileStream.Read(readBuff, 0, 8);
            Size = BitConverter.ToInt64(readBuff, 0);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            FirstChunkpos = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            Options.FirstChunksize = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            LastChunkpos = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            Options.LastChunksize = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            Chunksize = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            int filenameLen = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[filenameLen];
            fileStream.Read(readBuff, 0, filenameLen);
            Filename = Encoding.UTF8.GetString(readBuff);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            int folderLen = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[folderLen];
            fileStream.Read(readBuff, 0, folderLen);
            Options.Folder = Encoding.UTF8.GetString(readBuff);

            readBuff = new byte[4];
            fileStream.Read(readBuff, 0, 4);
            int partFolderLen = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[partFolderLen];
            fileStream.Read(readBuff, 0, partFolderLen);
            Options.PartFolder = Encoding.UTF8.GetString(readBuff);

            headersSize = (int) fileStream.Position;
            Options.PartExtension = (new FileInfo(partfile)).Extension;

            CalculatePartsize();

            MapChunkIdToChunkPos = new Dictionary<int, int>();

            int curChunkSize;
            readBuff = new byte[4];

            do
            {
                curChunkPos++;

                if (curChunkPos == FirstChunkpos)
                    curChunkSize = Options.FirstChunksize;
                else if (curChunkPos == LastChunkpos)
                    curChunkSize = Options.LastChunksize;
                else
                    curChunkSize = Chunksize;

                if (fileStream.Position + 4 + curChunkSize > fileStream.Length) { curChunkPos--; break; }

                fileStream.Read(readBuff, 0, 4);
                int chunkId = BitConverter.ToInt32(readBuff, 0);
                MapChunkIdToChunkPos.Add(chunkId, curChunkPos);

                fileStream.Position += curChunkSize;
            } while (true);

            if (Options.AutoCreate && fileStream.Length == Partsize) Create();
        }

        /// <summary>
        /// Writes a middle chunk with 'Chunksize' length
        /// </summary>
        /// <param name="chunkId">The zero-based chunk id of the completed file</param>
        /// <param name="chunk">Chunk data</param>
        /// <param name="offset">The zero-based offset</param>
        public void Write(int chunkId, byte[] chunk, int offset = 0)
        {
            lock (lockRW)
            {
                if (!ValidateWrite(chunkId)) return;
                WriteChunk(chunkId, chunk, offset, Chunksize);
                if (Options.AutoCreate && fileStream.Length == Partsize) Create();
            }
        }
        /// <summary>
        /// Writes the first chunk (0) of the completed file
        /// </summary>
        /// <param name="chunk">Chunk data</param>
        /// <param name="offset">The zero-based offset</param>
        /// <param name="len">Length</param>
        public void WriteFirst(byte[] chunk, int offset = 0, int len = -1)
        {
            lock (lockRW)
            {
                if (len == -1) len = chunk.Length;

                if (!ValidateWrite(0)) return;

                // Save firstPos / firstChunkSize
                long savePos = fileStream.Position;
                fileStream.Seek(3 + 8 + 8,  SeekOrigin.Begin);
                fileStream.Write(BitConverter.GetBytes(curChunkPos + 1),0, sizeof(int));
                fileStream.Write(BitConverter.GetBytes(len),            0, sizeof(int));
                fileStream.Seek(savePos,SeekOrigin.Begin);

                WriteChunk(0, chunk, offset, len);

                FirstChunkpos           = curChunkPos;
                Options.FirstChunksize  = len;
                CalculatePartsize();

                if (Options.AutoCreate && fileStream.Length == Partsize) Create();
            }
        }
        /// <summary>
        /// Writes the last chunk of the completed file (<=chunksize)
        /// </summary>
        /// <param name="chunkId">The zero-based chunk id of the completed file</param>
        /// <param name="chunk">Chunk data</param>
        /// <param name="offset">The zero-based offset</param>
        /// <param name="len">Length</param>
        public void WriteLast(int chunkId, byte[] chunk, int offset = 0, int len = -1)
        {
            lock (lockRW)
            {
                if (chunkId == 0) { WriteFirst(chunk, offset, len); return; }

                if (len == -1) len = chunk.Length;
                if (!ValidateWrite(chunkId)) return;

                // Save lastPos / lastChunkSize
                long savePos = fileStream.Position;
                fileStream.Seek(3 + 8 + 8 + 4 + 4,  SeekOrigin.Begin);
                fileStream.Write(BitConverter.GetBytes(curChunkPos + 1),0, sizeof(int));
                fileStream.Write(BitConverter.GetBytes(len),            0, sizeof(int));
                fileStream.Seek(savePos,        SeekOrigin.Begin);

                WriteChunk(chunkId, chunk, offset, len);

                LastChunkpos            = curChunkPos;
                Options.LastChunksize   = len;
                CalculatePartsize();

                if (Options.AutoCreate && fileStream.Length == Partsize) Create();
            }
        }

        /// <summary>
        /// Reads the specified byte range from the part file
        /// </summary>
        /// <param name="pos">The zero-based position to start reading</param>
        /// <param name="len">The length of the bytes to retrieve</param>
        /// <returns>Byte data</returns>
        /// <exception cref="Exception">Thrown when the first chunk size is not known yet</exception>
        public byte[] Read(long pos, long len)
        {
            lock (lockCreate)
            {
                byte[] readData = null;

                if (Created)
                {
                    readData = new byte[len];
                    fileStream.Seek(pos, SeekOrigin.Begin);
                    fileStream.Read(readData, 0, (int)len);
                    return readData;
                }
                
                // We could possible allow reading by guessing or considering FirstChunkSize = 0 (if we dont care about exact position)
                //if (Options.FirstChunksize == -1) { Warnings?.Invoke(this, new WarningEventArgs("Cannot read data until first chunk size will be known")); return null; }
                if (Options.FirstChunksize == -1) ThrowException($"First chunk size is not known yet");

                long readsize;
                long startByte;
                byte[] curChunk;
                long sizeLeft = len;

                int chunkId     = (int)((pos  - Options.FirstChunksize)     / Chunksize) + 1;
                int lastChunkId = (int)((Size - Options.FirstChunksize - 1) / Chunksize) + 1;
                
                if (pos < Options.FirstChunksize)
                {
                    chunkId     = 0;
                    startByte   = pos;
                    readsize    = Math.Min(sizeLeft, Options.FirstChunksize - startByte);
                }
                else if (chunkId == lastChunkId && LastChunkpos != -1)
                {
                    startByte   = ((pos - Options.FirstChunksize) % Chunksize);
                    readsize    = Math.Min(sizeLeft, Options.LastChunksize - startByte);
                }
                else
                {
                    startByte   = ((pos - Options.FirstChunksize) % Chunksize);
                    readsize    = Math.Min(sizeLeft, Chunksize - startByte);
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    curChunk = ReadChunk(chunkId);
                    ms.Write(curChunk, (int)startByte, (int)readsize); //  Utils.ArraySub(ref curChunk, (uint)startByte, (uint)readsize);
                    sizeLeft -= readsize;

                    while (sizeLeft > 0)
                    {
                        chunkId++;

                        curChunk = ReadChunk(chunkId);
                        if (chunkId == lastChunkId && LastChunkpos != -1)
                            readsize = (uint)Math.Min(sizeLeft, Options.LastChunksize);
                        else
                            readsize = (uint)Math.Min(sizeLeft, Chunksize);

                        ms.Write(curChunk, 0, (int)readsize); //readData = Utils.ArrayMerge(readData, Utils.ArraySub(ref curChunk, 0, (uint)readsize));
                        sizeLeft -= readsize;
                    }

                    readData = ms.ToArray();
                }

                return readData;
            }
        }
        /// <summary>
        /// Reads the specified chunk id from the part file
        /// </summary>
        /// <param name="chunkId">The chunk id of the completed file</param>
        /// <returns>Byte data</returns>
        /// <exception cref="Exception">Thrown when the specified chunk id is not available</exception>
        public byte[] ReadChunk(int chunkId)
        {
            lock (lockRW)
            {
                if (!MapChunkIdToChunkPos.ContainsKey(chunkId)) ThrowException($"Chunk id {chunkId} not available yet");

                int chunkPos   = MapChunkIdToChunkPos[chunkId];
                int chunksLeft = chunkPos;

                // Find chunk's size
                int chunksize = Chunksize;
                if (chunkPos == FirstChunkpos)
                    chunksize = Options.FirstChunksize;
                else if (chunkPos == LastChunkpos)
                    chunksize = Options.LastChunksize;

                // Find chunk's position in file
                long filePos    = headersSize + 4;
                if (FirstChunkpos != -1 && chunkPos > FirstChunkpos) { filePos += 4 + Options.FirstChunksize; chunksLeft--; }
                if (LastChunkpos  != -1 && chunkPos > LastChunkpos ) { filePos += 4 + Options.LastChunksize;  chunksLeft--; }
                filePos += (long)chunksLeft * (Chunksize + 4);

                // Read at filePos len of chunksize
                if (filePos < fileStream.Length)
                {
                    byte[] data = new byte[chunksize];
                    long savePos = fileStream.Position;
                    try
                    {
                        fileStream.Seek(filePos, SeekOrigin.Begin);
                        fileStream.Read(data, 0, chunksize);
                        fileStream.Seek(savePos, SeekOrigin.Begin);
                        return data;
                    }
                    catch (Exception e) { fileStream.Seek(savePos, SeekOrigin.Begin); throw e; }
                }
            }

            // pos >= fileStream.Length
            throw new Exception("Data not available");
        }

        /// <summary>
        /// Manually forces to create the completed file.
        /// Warning: If not all chunks have been written, it will crash! (See Options.AutoCreate)
        /// </summary>
        public void Create()
        {
            lock (lockCreate)
            {
                lock (lockRW)
                {
                    if (Created) return;

                    FileCreating?.Invoke(this, EventArgs.Empty);
                    using (FileStream fs = File.Open(Path.Combine(Options.Folder, Filename), FileMode.CreateNew))
                    {
                        if (Size > 0)
                        {
                            for (int i=0; i<MapChunkIdToChunkPos.Count; i++)
                            {
                                byte[] data = ReadChunk(i);
                                fs.Write(data, 0, data.Length);
                            }
                        }
                    }

                    fileStream.Close();
                    Created = true;
                    
                    if (Options.DeletePartOnCreate)
                        File.Delete(Path.Combine(Options.PartFolder, Filename + Options.PartExtension));
                    
                    FileCreated?.Invoke(this, EventArgs.Empty);

                    if (Options.StayAlive)
                        fileStream = File.Open(Path.Combine(Options.Folder, Filename), FileMode.Open, FileAccess.Read);
                    else
                        Dispose();
                }
            }
        }

        /// <summary>
        /// Closes the file input and deletes part and/or completed files if specified by the options
        /// </summary>
        public void Dispose()
        {
            if (Disposed) return;

            if (fileStream != null)
            {
                fileStream.Flush();
                fileStream.Close();
            }

            if (Options.DeletePartOnDispose && File.Exists(Path.Combine(Options.PartFolder, Filename + Options.PartExtension)))
                File.Delete(Path.Combine(Options.PartFolder, Filename + Options.PartExtension));

            if (Options.DeleteOnDispose && File.Exists(Path.Combine(Options.Folder, Filename)))
                File.Delete(Path.Combine(Options.Folder, Filename));

            MapChunkIdToChunkPos= null;
            Options             = null;
            fileStream          = null;
            Disposed            = true;
        }

        private void CalculatePartsize()
        {
            Partsize        = -1;
            ChunksTotal     = -1;

            // We need at least first or last chunk size to calculate
            if (Options.FirstChunksize == -1 && Options.LastChunksize == -1) return;

            // Calculate first chunk size
            if (Options.FirstChunksize == -1 && Options.LastChunksize != -1)
            {
                Options.FirstChunksize = (int) ((Size - Options.LastChunksize) % Chunksize);
                if (Options.FirstChunksize == 0) Options.FirstChunksize = Chunksize;
            }

            // One chunk file | We dont have last chunk (it is only one chunk / first chunk size)
            if (Options.FirstChunksize == Size)
            {
                Partsize    = headersSize + 4 + Options.FirstChunksize;
                ChunksTotal = 1;
                return;
            }

            // Calculate last chunk size
            if (Options.FirstChunksize != -1 && Options.LastChunksize == -1)
            {
                Options.LastChunksize = (int) ((Size - Options.FirstChunksize) % Chunksize);
                if (Options.LastChunksize == 0) Options.LastChunksize = Chunksize;
            }

            // Two chunks file
            if (Size == Options.FirstChunksize + Options.LastChunksize)
            {
                Partsize    = headersSize + 8 + Options.FirstChunksize + Options.LastChunksize;
                ChunksTotal = 2;
                return;
            }

            Partsize    = headersSize + 8 + Options.FirstChunksize + Options.LastChunksize;
            ChunksTotal = 2;
            long szLeft = Size - (Options.FirstChunksize + Options.LastChunksize);
            
            if (szLeft % Chunksize != 0) ThrowException($"Invalid first and/or last chunk size [First: {Options.FirstChunksize}, Last: {Options.LastChunksize}]");

            Partsize    += (((szLeft - 1) / Chunksize) + 1) * (4 + Chunksize);
            ChunksTotal += (int) ((szLeft - 1) / Chunksize) + 1;
        }
        private void ThrowException(string msg) { throw new Exception($"Partfile '{Filename}': {msg}"); }
        private bool ValidateWrite(int chunkId)
        {
            if (Created) { Warnings?.Invoke(this, new WarningEventArgs("File has already been created")); return false; }
            if (MapChunkIdToChunkPos.ContainsKey(chunkId)) { Warnings?.Invoke(this, new WarningEventArgs($"Chunk with id {chunkId} has already been written")); return false; }

            return true;
        }
        private void WriteChunk(int chunkId, byte[] chunk, int offset, int len)
        {
            fileStream.Write(BitConverter.GetBytes(chunkId), 0, sizeof(int));
            fileStream.Write(chunk, offset, len);
            fileStream.Flush();
            curChunkPos++;
            MapChunkIdToChunkPos.Add(chunkId, curChunkPos);
        }
        private void WriteHeaders()
        {
            fileStream.Write(Encoding.UTF8.GetBytes("APF"),     0, 3);
            fileStream.Write(BitConverter.GetBytes(Version.Major),          0, sizeof(int));
            fileStream.Write(BitConverter.GetBytes(Version.Minor),          0, sizeof(int));

            fileStream.Write(BitConverter.GetBytes(Size),                   0, sizeof(long));
            fileStream.Write(BitConverter.GetBytes(FirstChunkpos),          0, sizeof(int));
            fileStream.Write(BitConverter.GetBytes(Options.FirstChunksize), 0, sizeof(int));
            fileStream.Write(BitConverter.GetBytes(LastChunkpos),           0, sizeof(int));
            fileStream.Write(BitConverter.GetBytes(Options.LastChunksize),  0, sizeof(int));
            fileStream.Write(BitConverter.GetBytes(Chunksize),              0, sizeof(int));

            byte[] filename     = Encoding.UTF8.GetBytes(Filename);
            byte[] folder       = Encoding.UTF8.GetBytes(Options.Folder);
            byte[] partFolder   = Encoding.UTF8.GetBytes(Options.PartFolder);
            fileStream.Write(BitConverter.GetBytes(filename.Length),        0, sizeof(int));
            fileStream.Write(filename,                                      0, filename.Length);
            fileStream.Write(BitConverter.GetBytes(folder.Length),          0, sizeof(int));
            fileStream.Write(folder,                                        0, folder.Length);
            fileStream.Write(BitConverter.GetBytes(partFolder.Length),      0, sizeof(int));
            fileStream.Write(partFolder,                                    0, partFolder.Length);

            fileStream.Flush();

            headersSize = (int) fileStream.Position;
        }

        FileStream      fileStream;

        readonly object lockRW      = new object();
        readonly object lockCreate  = new object();
        int             curChunkPos = -1;
        int             headersSize = -1;
    }
}
