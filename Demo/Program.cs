using System;

using SuRGeoNix.Partfiles;

namespace Demo
{
    class Program
    {
        static void Main(string[] args)
        {
            // TODO Demo

            Partfile pf1 = new Partfile(@"C:\Users\Owner\AppData\Local\Temp\test01.part");
            //pf1.FileCreating += Pf1_FileCreating;
            //pf1.FileCreated += Pf1_FileCreated;
            //pf1.WriteFirst(CreateBytes(20, 1));
            //Console.WriteLine(pf1.Created);
            //return;

            Options opt = new Options();
            
            opt.FirstChunksize  = 20;
            //opt.LastChunksize   = 80;
            //opt.DeleteOnDispose      = true;
            opt.Overwrite       = true;
            opt.PartOverwrite   = true;

            Partfile pf = new Partfile("test01", 100, 220, opt);

            pf.Write(2, CreateBytes(100, 3));
            pf.Write(1, CreateBytes(100, 2));
            //byte[] t1 = pf.Read(21, 20);
            
            //pf.WriteFirst(CreateBytes(20, 1));
            pf.Dispose();
            //pf.WriteLast(2, CreateBytes(80, 3));
            //Console.WriteLine(pf.Created);
        }

        private static void Pf1_FileCreated(Partfile partfile, EventArgs e)
        {
            Console.WriteLine($"{partfile.Filename} created");
        }

        private static void Pf1_FileCreating(Partfile partfile, EventArgs e)
        {
            Console.WriteLine($"{partfile.Filename} creating ...");
        }

        static byte[] CreateBytes(int len, byte value)
        {
            byte[] ret = new byte[len];
            for (int i=0; i<len; i++)
                ret[i] = value;

            return ret;
        }
    }
}
