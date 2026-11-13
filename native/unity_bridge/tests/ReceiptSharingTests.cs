using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using JiangShen.AssistantRuntime;

// Actual production ReceiptStore, independent owned Windows files. No Unity,
// game assemblies, process reads, hooks or business commands are involved.
internal static class ReceiptSharingTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static string ReadHeld(FileStream held)
    {
        held.Position = 0; byte[] bytes = new byte[(int)held.Length]; int count = 0;
        while (count < bytes.Length) { int read = held.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read; }
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
    private static string Document(int sequence)
    { return "{\"sequence\":" + sequence + ",\"padding\":\"" + new string((char)('a' + sequence % 26), 2048) + "\"}"; }
    public static int Main(string[] args)
    {
        string root = args[0]; Directory.CreateDirectory(root); ReceiptStore store = new ReceiptStore(root);
        try
        {
            foreach (string name in new string[] { "state.json", "ack.json" })
            {
                string path = Path.Combine(root, name), previous = Document(0);
                Check(!File.Exists(path), name + "_first_absent"); store.Write(name, previous);
                Check(File.ReadAllText(path, Encoding.UTF8) == previous + "\n", name + "_first_publish");
                for (int i = 1; i <= 100; i++)
                {
                    string next = Document(i);
                    using (FileStream held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        Check(ReadHeld(held) == previous + "\n", name + "_old_initial_complete");
                        store.Write(name, next); // The exact production function under an open target reader.
                        Check(ReadHeld(held) == previous + "\n", name + "_old_object_preserved");
                        Check(File.ReadAllText(path, Encoding.UTF8) == next + "\n", name + "_new_path_complete");
                        Check(!File.Exists(path + ".tmp"), name + "_replacement_consumed");
                    }
                    previous = next;
                }
                string blocked = Document(101); bool rejected = false;
                using (FileStream held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    try { store.Write(name, blocked); } catch (IOException) { rejected = true; }
                    Check(rejected, name + "_no_delete_share_rejected");
                    Check(ReadHeld(held) == previous + "\n", name + "_failed_old_object_unchanged");
                    Check(File.ReadAllText(path, Encoding.UTF8) == previous + "\n", name + "_failed_target_unchanged");
                    Check(File.ReadAllText(path + ".tmp", Encoding.UTF8) == blocked + "\n", name + "_failed_temp_retained");
                }
                store.Write(name, blocked);
                Check(File.ReadAllText(path, Encoding.UTF8) == blocked + "\n", name + "_publish_after_reader_close");
            }
            store.Close();
            Console.WriteLine("{\"status\":\"PASS\",\"checks\":" + checks + ",\"actual_production_receipt_store\":true,\"first_publish_cases\":2,\"held_share7_replacements\":200,\"held_share3_failures\":2,\"old_handle_old_complete_new_path_new_complete\":true,\"game_access\":false}"); return 0;
        }
        catch (Exception error) { store.Close(); Console.Error.WriteLine(error); return 1; }
    }
}
