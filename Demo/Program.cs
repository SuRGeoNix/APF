using System;

using SuRGeoNix.Partfiles;

namespace Demo
{
    class Program
    {
        static void Main(string[] args)
        {
            // TODO Demo

            
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
