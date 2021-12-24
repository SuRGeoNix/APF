using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace SuRGeoNix.Partfiles
{
    /// <summary>
    /// Thread-safe read-only stream to access partfile's data
    /// </summary>
    public class PartStream : Stream
    {
        private Partfile partFile;
        public PartStream(Partfile partFile) { this.partFile = partFile; this.Length = partFile.Size; Position = 0; }

        public override bool CanRead    => true;
        public override bool CanSeek    => true;
        public override bool CanWrite   => false;
        public override long Length     { get; }
        public override long Position   { get; set; }

#if NET5_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            int ret = partFile.Read(Position, buffer);
            if (ret < 0) return ret;

            Position += ret;
            return ret;
        }
#endif

        public override int Read(byte[] buffer, int offset, int count)
        {
            int ret = partFile.Read(Position, buffer, offset, count);
            if (ret < 0) return ret;

            Position += ret;
            return ret;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin) {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;
                case SeekOrigin.Current:
                    Position += offset;
                    break;
                case SeekOrigin.End:
                    Position = Length - offset;
                    break;
                default:
                    throw new NotSupportedException ();
            }

            return Position;
        }

        public override void Flush() { throw new NotImplementedException(); }
        public override void SetLength(long value) { throw new NotImplementedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotImplementedException(); }
    }

    /// <summary>
    /// Advanced Partfiles (thread-safe read/write)
    /// </summary>
    public class Partfile
    {
        FileStream      writeStream;
        FileStream      readStream;
        readonly object lockCreate  = new object();
        int             curChunkPos = -1;
        int             headersSize = -1;

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

        public ConcurrentDictionary<int, int> MapChunkIdToChunkPos { get; private set; }

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

            // Allow Empty Files
            if (size == 0)
            {
                if (File.Exists(Path.Combine(Options.Folder, Filename)) && !Options.Overwrite) ThrowException($"Exists already in {Options.Folder}");
                File.Create(Path.Combine(Options.Folder, Filename));

                return;
            }

            // Validation Sizes
            if (Chunksize < 1)                      ThrowException("Chunksize must be greater than zero");
            if (Size != -1 && Size < 1)             ThrowException("Size must be greater than zero");
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
                writeStream = File.Open(Path.Combine(Options.PartFolder, Filename) + Options.PartExtension, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                readStream  = File.Open(Path.Combine(Options.PartFolder, Filename) + Options.PartExtension, FileMode.Open, FileAccess.Read, FileShare.Write);
                WriteHeaders();
                CalculatePartsize();
            } catch (Exception e) { ThrowException(e.Message); }

            MapChunkIdToChunkPos = new ConcurrentDictionary<int, int>();
        }

        /// <summary>
        /// Loads an existing partfile
        /// </summary>
        /// <param name="partfile">Absolute path of the existing part file</param>
        /// <param name="options">Warning: main options will be used from the saved part file</param>
        public Partfile(string partfile, Options options = null) : this(partfile, false, options) { }

        /// <summary>
        /// Loads an existing partfile
        /// </summary>
        /// <param name="partfile">Absolute path of the existing part file</param>
        /// <param name="forceOptionsFolder">Changes the previously defined folder with the new one from Options.Folder</param>
        /// <param name="options">Warning: main options will be used from the saved part file</param>
        public Partfile(string partfile, bool forceOptionsFolder, Options options = null)
        {
            if (!File.Exists(partfile)) throw new Exception($"Partfile '{partfile}' does not exist");

            Options = options == null ? new Options() : (Options) options.Clone();

            readStream = File.Open(partfile, FileMode.Open, FileAccess.Read, FileShare.Write);

            // Type
            byte[] readBuff = new byte[3];
            readStream.Read(readBuff, 0, 3);

            if (Encoding.UTF8.GetString(readBuff) != "APF") throw new IOException($"Invalid headers in partfile");

            // Major
            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);

            // Minor
            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);

            readBuff = new byte[8];
            readStream.Read(readBuff, 0, 8);
            Size = BitConverter.ToInt64(readBuff, 0);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            FirstChunkpos = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            Options.FirstChunksize = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            LastChunkpos = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            Options.LastChunksize = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            Chunksize = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            int filenameLen = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[filenameLen];
            readStream.Read(readBuff, 0, filenameLen);
            Filename = Encoding.UTF8.GetString(readBuff);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            int folderLen = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[folderLen];
            readStream.Read(readBuff, 0, folderLen);
            if (!forceOptionsFolder) Options.Folder = Encoding.UTF8.GetString(readBuff);

            readBuff = new byte[4];
            readStream.Read(readBuff, 0, 4);
            int partFolderLen = BitConverter.ToInt32(readBuff, 0);

            readBuff = new byte[partFolderLen];
            readStream.Read(readBuff, 0, partFolderLen);
            Options.PartFolder = Encoding.UTF8.GetString(readBuff);

            headersSize = (int) readStream.Position;
            Options.PartExtension = (new FileInfo(partfile)).Extension;

            // Validation Overwrite
            if (File.Exists(Path.Combine(Options.Folder, Filename)))
            {
                if (!Options.Overwrite) { readStream.Close(); readStream = null; ThrowException($"Exists already in {Options.Folder}"); }
                File.Delete(Path.Combine(Options.Folder, Filename));
            }

            // Create Folder
            Directory.CreateDirectory((new FileInfo(Path.Combine(Options.Folder, Filename))).DirectoryName);

            CalculatePartsize();

            // Load Map from file (TODO: check last block for possible corruption and delete it if thats the case)
            MapChunkIdToChunkPos = new ConcurrentDictionary<int, int>();

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

                if (readStream.Position + 4 + curChunkSize > readStream.Length) { curChunkPos--; break; }

                readStream.Read(readBuff, 0, 4);
                int chunkId = BitConverter.ToInt32(readBuff, 0);
                MapChunkIdToChunkPos.TryAdd(chunkId, curChunkPos);

                readStream.Position += curChunkSize;
            } while (true);

            if (Options.AutoCreate && readStream.Length == Partsize)
                Create();
            else
            {
                writeStream = File.Open(partfile, FileMode.Open, FileAccess.Write, FileShare.Read);
                writeStream.Position = writeStream.Length;
            }
        }

        /// <summary>
        /// Writes a middle chunk with 'Chunksize' length
        /// </summary>
        /// <param name="chunkId">The zero-based chunk id of the completed file</param>
        /// <param name="chunk">Chunk data</param>
        /// <param name="offset">The zero-based offset</param>
        public void Write(int chunkId, byte[] chunk, int offset = 0)
        {
            if (!ValidateWrite(chunkId)) return;
            WriteChunk(chunkId, chunk, offset, Chunksize);
            if (Options.AutoCreate && writeStream.Length == Partsize) Create();
        }
        /// <summary>
        /// Writes the first chunk (0) of the completed file
        /// </summary>
        /// <param name="chunk">Chunk data</param>
        /// <param name="offset">The zero-based offset</param>
        /// <param name="len">Length</param>
        public void WriteFirst(byte[] chunk, int offset = 0, int len = -1)
        {
            if (len == -1) len = chunk.Length;

            if (!ValidateWrite(0)) return;

            // Save firstPos / firstChunkSize
            long savePos = writeStream.Position;
            writeStream.Seek(3 + 8 + 8,  SeekOrigin.Begin);
            writeStream.Write(BitConverter.GetBytes(curChunkPos + 1),0, sizeof(int));
            writeStream.Write(BitConverter.GetBytes(len),            0, sizeof(int));
            writeStream.Seek(savePos,SeekOrigin.Begin);

            WriteChunk(0, chunk, offset, len);

            FirstChunkpos           = curChunkPos;
            Options.FirstChunksize  = len;
            CalculatePartsize();

            if (Options.AutoCreate && writeStream.Length == Partsize) Create();
        }
        /// <summary>
        /// Writes the last chunk of the completed file (&lt;=chunksize)
        /// </summary>
        /// <param name="chunkId">The zero-based chunk id of the completed file</param>
        /// <param name="chunk">Chunk data</param>
        /// <param name="offset">The zero-based offset</param>
        /// <param name="len">Length</param>
        public void WriteLast(int chunkId, byte[] chunk, int offset = 0, int len = -1)
        {
            if (chunkId == 0) { WriteFirst(chunk, offset, len); return; }

            if (len == -1) len = chunk.Length;
            if (!ValidateWrite(chunkId)) return;

            // Save lastPos / lastChunkSize
            long savePos = writeStream.Position;
            writeStream.Seek(3 + 8 + 8 + 4 + 4,  SeekOrigin.Begin);
            writeStream.Write(BitConverter.GetBytes(curChunkPos + 1),0, sizeof(int));
            writeStream.Write(BitConverter.GetBytes(len),            0, sizeof(int));
            writeStream.Seek(savePos,        SeekOrigin.Begin);

            WriteChunk(chunkId, chunk, offset, len);

            LastChunkpos            = curChunkPos;
            Options.LastChunksize   = len;
            CalculatePartsize();

            if (Options.AutoCreate && writeStream.Length == Partsize) Create();
        }

        /// <summary>
        /// Reads the specified actual bytes from the part file
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public int Read(long pos, byte[] buffer, int offset, int count)
        {
            lock (lockCreate)
            {
                if (Created)
                {
                    readStream.Position = pos;
                    return readStream.Read(buffer, offset, count);
                }

                // We could possible allow reading by guessing or considering FirstChunkSize = 0 (if we dont care about exact pos)
                //if (Options.FirstChunksize == -1) { Warnings?.Invoke(this, new WarningEventArgs("Cannot read data until first chunk size will be known")); return null; }
                if (pos + count > Size) count = (int) (Size - pos);
                //BeforeReading?.Invoke(this, new BeforeReadingEventArgs(pos, count));

                if (Options.FirstChunksize == -1) ThrowException($"First chunk size is not known yet");

                int readsize;
                int readsizeTotal;
                int startByte;

                int sizeLeft    = count;
                int chunkId     = (int)((pos  - Options.FirstChunksize) / Chunksize) + 1;

                if (pos < Options.FirstChunksize) //if (chunkId == 0)
                {
                    chunkId = 0;
                    startByte   = (int) pos;
                    readsize    = Math.Min(sizeLeft, Options.FirstChunksize - startByte);
                }
                else if (chunkId == ChunksTotal - 1)
                {
                    startByte   = (int) ((pos - Options.FirstChunksize) % Chunksize);
                    readsize    = Math.Min(sizeLeft, Options.LastChunksize - startByte);
                }
                else
                {
                    startByte   = (int) ((pos - Options.FirstChunksize) % Chunksize);
                    readsize    = Math.Min(sizeLeft, Chunksize - startByte);
                }

                readsize        = ReadChunk(chunkId, startByte, buffer, offset, readsize);
                sizeLeft       -= readsize;
                readsizeTotal   = readsize;

                while (sizeLeft > 0)
                {
                    chunkId++;
                    readsize        = chunkId == ChunksTotal - 1 ? readsize = Math.Min(sizeLeft, Options.LastChunksize) : Math.Min(sizeLeft, Chunksize);
                    readsize        = ReadChunk(chunkId, 0, buffer, offset + readsizeTotal, readsize);
                    sizeLeft       -= readsize;
                    readsizeTotal  += readsize;
                }

                pos += readsizeTotal;
                return readsizeTotal;
            }
        }

#if NET5_0_OR_GREATER
        public int Read(long pos, Span<byte> buffer)
        {
            lock (lockCreate)
            {
                if (Created)
                {
                    readStream.Position = pos;
                    return readStream.Read(buffer);
                }

                int count = buffer.Length;

                // We could possible allow reading by guessing or considering FirstChunkSize = 0 (if we dont care about exact pos)
                //if (Options.FirstChunksize == -1) { Warnings?.Invoke(this, new WarningEventArgs("Cannot read data until first chunk size will be known")); return null; }
                if (pos + count > Size) count = (int) (Size - pos);
                //BeforeReading?.Invoke(this, new BeforeReadingEventArgs(pos, count));

                if (Options.FirstChunksize == -1) ThrowException($"First chunk size is not known yet");

                int readsize;
                int readsizeTotal;
                int startByte;

                int sizeLeft    = count;
                int chunkId     = (int)((pos  - Options.FirstChunksize) / Chunksize) + 1;

                if (pos < Options.FirstChunksize) //if (chunkId == 0)
                {
                    chunkId = 0;
                    startByte   = (int) pos;
                    readsize    = Math.Min(sizeLeft, Options.FirstChunksize - startByte);
                }
                else if (chunkId == ChunksTotal - 1)
                {
                    startByte   = (int) ((pos - Options.FirstChunksize) % Chunksize);
                    readsize    = Math.Min(sizeLeft, Options.LastChunksize - startByte);
                }
                else
                {
                    startByte   = (int) ((pos - Options.FirstChunksize) % Chunksize);
                    readsize    = Math.Min(sizeLeft, Chunksize - startByte);
                }

                readsize        = ReadChunk(chunkId, startByte, buffer.Slice(0, readsize));
                sizeLeft       -= readsize;
                readsizeTotal   = readsize;

                while (sizeLeft > 0)
                {
                    chunkId++;
                    readsize        = chunkId == ChunksTotal - 1 ? readsize = Math.Min(sizeLeft, Options.LastChunksize) : Math.Min(sizeLeft, Chunksize);
                    readsize        = ReadChunk(chunkId, 0, buffer.Slice(readsizeTotal, readsize));
                    sizeLeft       -= readsize;
                    readsizeTotal  += readsize;
                }

                pos += readsizeTotal;
                return readsizeTotal;
            }
        }
#endif

        /// <summary>
        /// Reads the specified chunkId bytes from the part file
        /// </summary>
        /// <param name="chunkId">The chunk id of the completed file</param>
        /// <param name="startByte">From which byte of the chunk will start the read</param>
        /// <param name="buffer">The output buffer</param>
        /// <param name="offset">The offset to start on the output buffer</param>
        /// <param name="count">How many bytes to read (Should ensure &lt;= ChunkSize or First/Last ChunkSize)</param>
        /// <returns></returns>
        public int ReadChunk(int chunkId, int startByte, byte[] buffer, int offset, int count)
        {
            if (!MapChunkIdToChunkPos.ContainsKey(chunkId)) ThrowException($"Chunk id {chunkId} not available yet");

            int chunkPos   = MapChunkIdToChunkPos[chunkId];
            int chunksLeft = chunkPos;

            // Find chunk's position in file
            long filePos = headersSize + 4;
            if (FirstChunkpos != -1 && chunkPos > FirstChunkpos) { filePos += 4 + Options.FirstChunksize; chunksLeft--; }
            if (LastChunkpos  != -1 && chunkPos > LastChunkpos ) { filePos += 4 + Options.LastChunksize;  chunksLeft--; }
            filePos += (long)chunksLeft * (Chunksize + 4);

            // Read at filePos len of chunksize
            readStream.Seek(filePos + startByte, SeekOrigin.Begin);
            return readStream.Read(buffer, offset, count);
        }

#if NET5_0_OR_GREATER
        public int ReadChunk(int chunkId, int startByte, Span<byte> buffer)
        {
            if (!MapChunkIdToChunkPos.ContainsKey(chunkId)) ThrowException($"Chunk id {chunkId} not available yet");

            int chunkPos   = MapChunkIdToChunkPos[chunkId];
            int chunksLeft = chunkPos;

            // Find chunk's position in file
            long filePos = headersSize + 4;
            if (FirstChunkpos != -1 && chunkPos > FirstChunkpos) { filePos += 4 + Options.FirstChunksize; chunksLeft--; }
            if (LastChunkpos  != -1 && chunkPos > LastChunkpos ) { filePos += 4 + Options.LastChunksize;  chunksLeft--; }
            filePos += (long)chunksLeft * (Chunksize + 4);

            // Read at filePos len of chunksize
            readStream.Seek(filePos + startByte, SeekOrigin.Begin);
            return readStream.Read(buffer);
        }
#endif

        /// <summary>
        /// Creates a new stream for read-only access of part data
        /// </summary>
        /// <returns></returns>
        public PartStream GetReadStream() { return new PartStream(this); }

        /// <summary>
        /// Manually forces to create the completed file.
        /// Warning: If not all chunks have been written, it will crash! (See Options.AutoCreate)
        /// </summary>
        public void Create()
        {
            lock (lockCreate)
            {
                if (Created) return;

                FileCreating?.Invoke(this, EventArgs.Empty);
                using (FileStream fs = File.Open(Path.Combine(Options.Folder, Filename), FileMode.CreateNew))
                {
                    if (Size > 0)
                    {
                        byte[] buffer = new byte[Options.FirstChunksize];
                        ReadChunk(0, 0, buffer, 0, Options.FirstChunksize);
                        fs.Write(buffer, 0, buffer.Length);

                        if (ChunksTotal > 1)
                        {
                            buffer = new byte[Chunksize];
                            for (int i=1; i<MapChunkIdToChunkPos.Count-1; i++)
                            {
                                ReadChunk(i, 0, buffer, 0, Chunksize);
                                fs.Write(buffer, 0, buffer.Length);
                            }
                            buffer = new byte[Options.LastChunksize];
                            ReadChunk(ChunksTotal-1, 0, buffer, 0, Options.LastChunksize);
                            fs.Write(buffer, 0, buffer.Length);
                        }
                    }
                }

                writeStream.Close();
                readStream.Close();
                writeStream = null;
                readStream = null;

                Created = true;
                    
                if (Options.DeletePartOnCreate)
                    File.Delete(Path.Combine(Options.PartFolder, Filename + Options.PartExtension));
                    
                FileCreated?.Invoke(this, EventArgs.Empty);

                if (Options.StayAlive)
                    readStream = File.Open(Path.Combine(Options.Folder, Filename), FileMode.Open, FileAccess.Read, FileShare.Read);
                else
                    Dispose();
            }
        }

        /// <summary>
        /// Closes the file input and deletes part and/or completed files if specified by the options
        /// </summary>
        public void Dispose()
        {
            if (Disposed) return;

            if (writeStream != null)
            {
                writeStream.Flush();
                writeStream.Close();
            }

            if (readStream != null)
                readStream.Close();

            if (Options.DeletePartOnDispose && File.Exists(Path.Combine(Options.PartFolder, Filename + Options.PartExtension)))
                File.Delete(Path.Combine(Options.PartFolder, Filename + Options.PartExtension));

            if (Options.DeleteOnDispose && File.Exists(Path.Combine(Options.Folder, Filename)))
                File.Delete(Path.Combine(Options.Folder, Filename));

            MapChunkIdToChunkPos= null;
            Options             = null;
            writeStream         = null;
            readStream          = null;
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
            writeStream.Write(BitConverter.GetBytes(chunkId), 0, sizeof(int));
            writeStream.Write(chunk, offset, len);
            writeStream.Flush();
            curChunkPos++;
            MapChunkIdToChunkPos.TryAdd(chunkId, curChunkPos);
        }
        private void WriteHeaders()
        {
            writeStream.Write(Encoding.UTF8.GetBytes("APF"),     0, 3);
            writeStream.Write(BitConverter.GetBytes(Version.Major),          0, sizeof(int));
            writeStream.Write(BitConverter.GetBytes(Version.Minor),          0, sizeof(int));

            writeStream.Write(BitConverter.GetBytes(Size),                   0, sizeof(long));
            writeStream.Write(BitConverter.GetBytes(FirstChunkpos),          0, sizeof(int));
            writeStream.Write(BitConverter.GetBytes(Options.FirstChunksize), 0, sizeof(int));
            writeStream.Write(BitConverter.GetBytes(LastChunkpos),           0, sizeof(int));
            writeStream.Write(BitConverter.GetBytes(Options.LastChunksize),  0, sizeof(int));
            writeStream.Write(BitConverter.GetBytes(Chunksize),              0, sizeof(int));

            byte[] filename     = Encoding.UTF8.GetBytes(Filename);
            byte[] folder       = Encoding.UTF8.GetBytes(Options.Folder);
            byte[] partFolder   = Encoding.UTF8.GetBytes(Options.PartFolder);
            writeStream.Write(BitConverter.GetBytes(filename.Length),        0, sizeof(int));
            writeStream.Write(filename,                                      0, filename.Length);
            writeStream.Write(BitConverter.GetBytes(folder.Length),          0, sizeof(int));
            writeStream.Write(folder,                                        0, folder.Length);
            writeStream.Write(BitConverter.GetBytes(partFolder.Length),      0, sizeof(int));
            writeStream.Write(partFolder,                                    0, partFolder.Length);

            writeStream.Flush();

            headersSize = (int) writeStream.Position;
        }
    }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member