using System;
using System.Data.SQLite;
using System.IO;
using MQTTnet;

namespace SpikeB1Sqlite
{
    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine("net10.0-windows SQLite/MQTTnet spike");

            string interopPath = Path.Combine(AppContext.BaseDirectory, "SQLite.Interop.dll");
            Console.WriteLine($"SQLite.Interop.dll present at output: {File.Exists(interopPath)} ({interopPath})");

            string dbPath = Path.Combine(Path.GetTempPath(), "spike_b1.sqlite");
            if (File.Exists(dbPath)) File.Delete(dbPath);

            using (var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, val TEXT)";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO t (val) VALUES ('hello')";
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT val FROM t WHERE id = 1";
                    object result = cmd.ExecuteScalar();
                    Console.WriteLine($"Round-trip read value: {result}");
                }
            }

            File.Delete(dbPath);

            var mqttFactory = new MqttFactory();
            using (var mqttClient = mqttFactory.CreateMqttClient())
            {
                Console.WriteLine($"MQTTnet client instantiated: {mqttClient != null}");
            }

            Console.WriteLine("SPIKE RESULT: PASS");
            return 0;
        }
    }
}
