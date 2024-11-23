using Core;

namespace ConsoleApp {
    internal class Program {
        static void Main() {

            GRIB draft = new();
            draft.DrawAllMaps("GRIBNOA00.000.1");
        }
    }
}
