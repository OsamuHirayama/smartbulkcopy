using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace HSBulkCopy
{
    enum PartitionType
    {
        Physical,
        Logical
    }

    abstract class CopyInfo
    {
        public string TableName;
        public int PartitionNumber;
        public abstract string GetPredicate();
    }

    class PhysicalPartitionCopyInfo : CopyInfo
    {
        public string PartitionFunction;
        public string PartitionColumn;

        public override string GetPredicate()
        {
            return $"$partition.{PartitionFunction}({PartitionColumn}) = {PartitionNumber}";
        }
    }

    class LogicalPartitionCopyInfo : CopyInfo
    {
        public int LogicalPartitionsCount;
        public override string GetPredicate()
        {
            return $"ABS(CAST(%%PhysLoc%% AS BIGINT)) % {LogicalPartitionsCount} = {PartitionNumber - 1}";
        }
    }

    class SmartBulkCopy
    {
        private readonly string _sourceConnectionString;
        private readonly string _destinationConnectionString;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly ConcurrentQueue<CopyInfo> _queue = new ConcurrentQueue<CopyInfo>();
        private readonly List<string> tablesToCopy = new List<string>();
        private int _maxTasks = 7;
        private int _logicalPartitionCount = 7;

        public SmartBulkCopy(string sourceConnectionString, string destinationConnectionString)
        {
            _sourceConnectionString = sourceConnectionString;
            _destinationConnectionString = destinationConnectionString;

            tablesToCopy.Add("dbo.LINEITEM");
            tablesToCopy.Add("dbo.ORDERS");
        }

        public async Task<int> Copy()
        {
            Console.WriteLine("Testing connections...");
            if (!TestConnection(_sourceConnectionString)) return 1;
            if (!TestConnection(_destinationConnectionString)) return 1;

            var copyInfo = new List<CopyInfo>();

            var conn = new SqlConnection(_sourceConnectionString);

            foreach (var t in tablesToCopy)
            {
                // TODO: Check it table exists

                // Check if table is partitioned
                var isPartitioned = CheckIfSourceTableIsPartitioned(t);

                // Create the Work Info data based on partitio lind
                if (isPartitioned)
                {
                    copyInfo.AddRange(CreatePhysicalPartitionedTableCopyInfo(t));
                }
                else
                {
                    copyInfo.AddRange(CreateLogicalPartitionedTableCopyInfo(t));
                }
            }

            Console.WriteLine("Enqueing work...");
            copyInfo.ForEach(ci => _queue.Enqueue(ci));
            Console.WriteLine($"{_queue.Count} items enqueued.");

            Console.WriteLine("Truncating destination tables...");
            tablesToCopy.ForEach(t => TruncateDestinationTable(t));

            var tasks = new List<Task>();
            Console.WriteLine($"Copying using {_maxTasks} parallel tasks.");
            foreach (var i in Enumerable.Range(1, _maxTasks))
            {
                tasks.Add(new Task(() => BulkCopy(i)));
            }
            Console.WriteLine($"Starting monitor...");
            var monitorTask = Task.Run(() => MonitorLogFlush());

            Console.WriteLine($"Start copying...");
            _stopwatch.Start();
            tasks.ForEach(t => t.Start());
            await Task.WhenAll(tasks.ToArray());
            _stopwatch.Stop();
            Console.WriteLine($"Done copying.");

            Console.WriteLine($"Waiting for monitor to shut down...");
            monitorTask.Wait();

            Console.WriteLine("Done in {0:#.00} secs", (double)_stopwatch.ElapsedMilliseconds / 1000.0);

            return 0;
        }

        private bool CheckIfSourceTableIsPartitioned(string tableName)
        {
            var conn = new SqlConnection(_sourceConnectionString);

            var isPartitioned = (int)conn.ExecuteScalar($@"
                    select 
                        IsPartitioned = case when count(*) > 1 then 1 else 0 end 
                    from 
                        sys.dm_db_partition_stats 
                    where 
                        [object_id] = object_id('{tableName}') 
                    and 
                        index_id in (0,1)
                    ");

            return (isPartitioned == 1);
        }

        private void TruncateDestinationTable(string tableName)
        {
            Console.WriteLine($"Truncating '{tableName}'...");
            var destinationConnection = new SqlConnection(_destinationConnectionString);
            destinationConnection.ExecuteScalar($"TRUNCATE TABLE {tableName}");
        }

        private List<CopyInfo> CreatePhysicalPartitionedTableCopyInfo(string tableName)
        {
            var copyInfo = new List<CopyInfo>();

            var conn = new SqlConnection(_sourceConnectionString);

            var partitionCount = (int)conn.ExecuteScalar($@"
                    select 
                        partitions = count(*) 
                    from 
                        sys.dm_db_partition_stats 
                    where 
                        [object_id] = object_id('{tableName}') 
                    and
                        index_id in (0,1)
                    ");

            Console.WriteLine($"Table {tableName} is partitioned. Bulk copy will be parallelized using {partitionCount} partition(s).");

            var partitionInfo = conn.QuerySingle($@"
                select 
                    pf.[name] as PartitionFunction,
                    c.[name] as PartitionColumn,
                    pf.[fanout] as PartitionCount
                from 
                    sys.indexes i 
                inner join
                    sys.partition_schemes ps on i.data_space_id = ps.data_space_id
                inner join
                    sys.partition_functions pf on ps.function_id = pf.function_id
                inner join
                    sys.index_columns ic on i.[object_id] = ic.[object_id] and i.index_id = ic.index_id
                inner join
                    sys.columns c on c.[object_id] = i.[object_id] and c.column_id = ic.column_id
                where 
                    i.[object_id] = object_id('{tableName}') 
                and 
                    i.index_id in (0,1)
                and
                    ic.partition_ordinal = 1
                ");

            foreach (var n in Enumerable.Range(1, partitionCount))
            {
                var cp = new PhysicalPartitionCopyInfo();
                cp.PartitionNumber = n;
                cp.TableName = tableName;
                cp.PartitionColumn = partitionInfo.PartitionColumn;
                cp.PartitionFunction = partitionInfo.PartitionFunction;

                copyInfo.Add(cp);

                //Console.WriteLine(cp.GetPredicate());
            }

            return copyInfo;
        }

        private List<CopyInfo> CreateLogicalPartitionedTableCopyInfo(string tableName)
        {
            Console.WriteLine($"Table {tableName} is NOT partitioned. Bulk copy will be parallelized using {_logicalPartitionCount} logical partitions.");

            var copyInfo = new List<CopyInfo>();

            foreach (var n in Enumerable.Range(1, _logicalPartitionCount))
            {
                var cp = new LogicalPartitionCopyInfo();
                cp.PartitionNumber = n;
                cp.TableName = tableName;
                cp.LogicalPartitionsCount = _logicalPartitionCount;

                copyInfo.Add(cp);

                //Console.WriteLine(cp.GetPredicate());
            }

            return copyInfo;

        }

        private void BulkCopy(int taskId)
        {
            CopyInfo copyInfo;
            Console.WriteLine($"Task {taskId}: Started...");

            while (_queue.TryDequeue(out copyInfo))
            {
                Console.WriteLine($"Task {taskId}: Processing table {copyInfo.TableName} partition {copyInfo.PartitionNumber}...");
                var sourceConnection = new SqlConnection(_sourceConnectionString);
                var sourceReader = sourceConnection.ExecuteReader($"SELECT * FROM {copyInfo.TableName} WHERE {copyInfo.GetPredicate()}");

                using (var bulkCopy = new SqlBulkCopy(_destinationConnectionString + $";Application Name=hsbulkcopy{taskId}", SqlBulkCopyOptions.TableLock))
                {
                    bulkCopy.BulkCopyTimeout = 0;
                    bulkCopy.BatchSize = 100000;
                    bulkCopy.DestinationTableName = copyInfo.TableName;

                    try
                    {
                        bulkCopy.WriteToServer(sourceReader);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                        var ie = ex.InnerException;
                        while (ie != null)
                        {
                            Console.WriteLine(ex.Message);
                            ie = ie.InnerException;
                        }
                    }
                    finally
                    {
                        sourceReader.Close();                        
                    }
                }
            }

            Console.WriteLine($"Task {taskId}: Done.");
        }
    
        private void MonitorLogFlush()
        {
            var conn = new SqlConnection(_destinationConnectionString + ";Application Name=hsbulk_log_monitor");
            var instance_name = (string)(conn.ExecuteScalar($"select instance_name from sys.dm_os_performance_counters where counter_name = 'Log Bytes Flushed/sec' and instance_name like '%-%-%-%-%'"));           

            string query = $@"
                declare @v1 bigint, @v2 bigint
                select @v1 = cntr_value from sys.dm_os_performance_counters 
                where counter_name = 'Log Bytes Flushed/sec' and instance_name = '{instance_name}';
                waitfor delay '00:00:05';
                select @v2 = cntr_value from sys.dm_os_performance_counters 
                where counter_name = 'Log Bytes Flushed/sec' and instance_name = '{instance_name}';
                select log_flush_mb_sec =  ((@v2-@v1) / 5.) / 1024. / 1024.
            ";
 
            while (true)
            {
                var log_flush = (decimal)(conn.ExecuteScalar(query));
                Console.WriteLine($"Log Flush Speed: {log_flush:00.00} MB/Sec");

                Task.Delay(5000);

                if (_queue.Count == 0) break;
            }
        }

        bool TestConnection(string connectionString)
        {
            var conn = new SqlConnection(connectionString);
            bool result = false;

            try {
                conn.Open();
                result = true;
            } 
            catch (Exception ex)
            {
                Console.WriteLine("Error while opening connection.");
                Console.WriteLine(ex.Message);
            } finally {
                conn.Close();
            }
            
            return result;    
        }
    }
}