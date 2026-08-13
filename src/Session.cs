using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security;
using ClearScada.Client;
using ClearScada.Client.Advanced;

namespace HistoryDeleter
{
    /// <summary>A single historic record as returned by the CDBHistoric query.</summary>
    internal sealed class HistoricRecord
    {
        /// <summary>
        /// The exact stored instant, decoded from RecordId, at the historian's full 100 ns
        /// resolution. This is what DeleteValue is given; it is never formatted and re-parsed.
        /// </summary>
        public DateTimeOffset Time;

        /// <summary>
        /// What the SQL "Time" column reported. The query layer rounds to the nearest millisecond,
        /// so this can be up to half a millisecond away from the real value and must never be used
        /// to address a record for deletion. Kept for the consistency check and for diagnostics.
        /// </summary>
        public DateTimeOffset SqlTime;

        /// <summary>False when RecordId could not be decoded and cross-checked; blocks deletion.</summary>
        public bool HasExactTime;

        public DateTimeOffset RecordTime;
        public object Value;
        public string FormattedValue;
        public int Quality;
        public string QualityDesc;
        public string ReasonDesc;
        public string StateDesc;
        public string StatusDesc;
        public object ModTime;
        public string ModUser;
        public string RecordId;

        // Display truncates rather than rounds, which is what ViewX shows for the same record.
        public string LocalText { get { return Time.ToLocalTime().ToString(TimeFormats.Display); } }
        public string UtcText { get { return Time.ToUniversalTime().ToString(TimeFormats.Display); } }
        public string ExactLocalText { get { return Time.ToLocalTime().ToString(TimeFormats.Exact); } }
        public string ExactUtcText { get { return Time.ToUniversalTime().ToString(TimeFormats.Exact); } }
    }

    internal static class TimeFormats
    {
        /// <summary>Millisecond display, matching how ViewX renders the same timestamp.</summary>
        public const string Display = "yyyy-MM-dd HH:mm:ss.fff";

        /// <summary>Full stored resolution, shown before anything is deleted.</summary>
        public const string Exact = "yyyy-MM-dd HH:mm:ss.fffffff";
    }

    internal sealed class PointMatch
    {
        public int Id;
        public string FullName;
        public string Aggregate;
        public override string ToString() { return FullName; }
    }

    internal sealed class ResolvedPoint
    {
        public int Id;
        public string FullName;
        public List<string> Aggregates = new List<string>();
    }

    internal sealed class DeleteFailure
    {
        public HistoricRecord Record;
        public string Message;
    }

    /// <summary>
    /// Owns the Geo SCADA connection and every server call the UI makes. All calls are serialised
    /// through a lock because the delete loop runs on a worker thread while the UI stays responsive.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        private readonly object _gate = new object();
        private IServer _server;

        public string Host { get; private set; }
        public int Port { get; private set; }
        public string UserName { get; private set; }
        public string ServerLabel { get; private set; }

        private Session() { }

        public static Session Connect(string host, int port, string userName, SecureString password)
        {
            ServerNode node = new ServerNode(host, port);
            node.ConnectTimeout = TimeSpan.FromSeconds(30);
            node.RequestTimeout = TimeSpan.FromMinutes(5);

            IServer server = node.Connect("Geo SCADA History Deleter");
            try
            {
                ((ISecurity)server).LogOn(userName, password, LogonFlags.None);
            }
            catch
            {
                Dispose(server);
                throw;
            }

            Session session = new Session();
            session._server = server;
            session.Host = host;
            session.Port = port;
            session.UserName = ((ISecurity)server).UserName;
            session.ServerLabel = server.ServerLabel;
            return session;
        }

        /// <summary>Finds the object by full name and reports which historic aggregates it carries.</summary>
        public ResolvedPoint ResolvePoint(string fullName)
        {
            lock (_gate)
            {
                List<QueryRow> rows = Query(
                    "SELECT Id, FullName FROM CDBObject WHERE FullName = ?",
                    new object[] { fullName }, 2);
                if (rows.Count == 0) return null;

                ResolvedPoint point = new ResolvedPoint();
                point.Id = Convert.ToInt32(rows[0].Data[0]);
                point.FullName = Convert.ToString(rows[0].Data[1]);

                foreach (QueryRow row in Query(
                    "SELECT AggrName FROM CDBHisBase WHERE Id = ?", new object[] { point.Id }, 64))
                {
                    string aggregate = Convert.ToString(row.Data[0]);
                    if (!string.IsNullOrEmpty(aggregate) && !point.Aggregates.Contains(aggregate))
                        point.Aggregates.Add(aggregate);
                }
                return point;
            }
        }

        /// <summary>Searches objects that actually have historic data, for the Browse dialog.</summary>
        public List<PointMatch> SearchPoints(string pattern, int maxRows)
        {
            lock (_gate)
            {
                List<PointMatch> matches = new List<PointMatch>();
                foreach (QueryRow row in Query(
                    "SELECT O.Id, O.FullName, H.AggrName FROM CDBObject O " +
                    "INNER JOIN CDBHisBase H ON O.Id = H.Id WHERE O.FullName LIKE ? ORDER BY O.FullName",
                    new object[] { pattern }, maxRows))
                {
                    PointMatch match = new PointMatch();
                    match.Id = Convert.ToInt32(row.Data[0]);
                    match.FullName = Convert.ToString(row.Data[1]);
                    match.Aggregate = Convert.ToString(row.Data[2]);
                    matches.Add(match);
                }
                return matches;
            }
        }

        /// <summary>
        /// Reads raw historic records over a time range, newest first. "Time" is quoted because TIME
        /// is a reserved word in Geo SCADA SQL, and the bounds are passed as parameters rather than
        /// interpolated.
        /// </summary>
        public List<HistoricRecord> ReadHistory(int objectId, DateTimeOffset start, DateTimeOffset end, int maxRows)
        {
            lock (_gate)
            {
                List<HistoricRecord> records = new List<HistoricRecord>();
                foreach (QueryRow row in Query(
                    "SELECT RecordId, \"Time\", RecordTime, Value, FormattedValue, Quality, QualityDesc, " +
                    "ReasonDesc, StateDesc, StatusDesc, ModTime, ModUser " +
                    "FROM CDBHistoric WHERE Id = ? AND \"Time\" >= ? AND \"Time\" <= ? ORDER BY \"Time\" DESC",
                    new object[] { objectId, start, end }, maxRows))
                {
                    HistoricRecord record = new HistoricRecord();
                    record.RecordId = Convert.ToString(row.Data[0]);
                    record.SqlTime = ToOffset(row.Data[1]);

                    DateTimeOffset exact;
                    record.HasExactTime = TryDecodeRecordTime(record.RecordId, record.SqlTime, out exact);
                    record.Time = exact;

                    record.RecordTime = ToOffset(row.Data[2]);
                    record.Value = row.Data[3];
                    record.FormattedValue = Convert.ToString(row.Data[4]);
                    record.Quality = row.Data[5] == null ? 0 : Convert.ToInt32(row.Data[5]);
                    record.QualityDesc = Convert.ToString(row.Data[6]);
                    record.ReasonDesc = Convert.ToString(row.Data[7]);
                    record.StateDesc = Convert.ToString(row.Data[8]);
                    record.StatusDesc = Convert.ToString(row.Data[9]);
                    record.ModTime = row.Data[10];
                    record.ModUser = Convert.ToString(row.Data[11]);
                    records.Add(record);
                }
                return records;
            }
        }

        /// <summary>
        /// Recovers a record's exact timestamp from its RecordId.
        ///
        /// This matters because the SQL "Time" column is rounded to the nearest millisecond, while
        /// the historian stores 100 ns ticks. A record actually stored at ...:56.8718856 is reported
        /// by SQL as ...:56.8720000, and DeleteValue given that rounded value matches nothing and
        /// silently deletes nothing. RecordId carries the true value: characters 8..24 are the
        /// instant as a hexadecimal Windows FILETIME.
        ///
        /// The decoded value is only accepted if it agrees with the SQL column to within a
        /// millisecond. If the id were ever laid out differently, the check fails, the record is
        /// flagged as having no exact time and the tool refuses to delete it, rather than issuing a
        /// delete against a timestamp it guessed.
        /// </summary>
        private static bool TryDecodeRecordTime(string recordId, DateTimeOffset sqlTime, out DateTimeOffset exact)
        {
            exact = sqlTime;
            if (string.IsNullOrEmpty(recordId) || recordId.Length < 24) return false;

            long fileTime;
            if (!long.TryParse(recordId.Substring(8, 16), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out fileTime) || fileTime <= 0)
                return false;

            DateTimeOffset candidate;
            try { candidate = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)); }
            catch (ArgumentOutOfRangeException) { return false; }

            if (Math.Abs((candidate - sqlTime).Ticks) > TimeSpan.TicksPerMillisecond) return false;

            exact = candidate;
            return true;
        }

        /// <summary>
        /// Permanently removes historic values at the given instants.
        ///
        /// This uses IHistory.DeleteHistoricData rather than CHistoryBase's DeleteValue method.
        /// DeleteValue is the more expressive of the two - it takes a source type and a comment that
        /// lands in the event journal - but invoking it through the generic object-method interface
        /// quantises its timestamp argument to a millisecond. Real field-collected samples sit on
        /// 100 ns ticks, so DeleteValue matches nothing for them and, worse, reports success anyway.
        /// That was confirmed against live data: exact, floored and ceiled millisecond values all
        /// left the record in place, while DeleteHistoricData removed it first time.
        /// </summary>
        public void DeleteHistoricValues(string pointFullName, DateTimeOffset[] times)
        {
            lock (_gate)
            {
                ((IHistory)_server).DeleteHistoricData(pointFullName, times);
            }
        }

        private List<QueryRow> Query(string sql, object[] arguments, int maxRows)
        {
            IQuery query = ((IQuerySource)_server).PrepareQuery(sql, new QueryParseParameters());
            QueryExecuteParameters parameters = new QueryExecuteParameters(arguments);
            parameters.MaxRowsToReturn = maxRows;

            QueryResult result = query.ExecuteSync(parameters);
            List<QueryRow> rows = new List<QueryRow>();
            if (result.Status == QueryStatus.NoDataFound) return rows;
            foreach (QueryRow row in result.Rows) rows.Add(row);
            return rows;
        }

        private static DateTimeOffset ToOffset(object value)
        {
            if (value is DateTimeOffset) return (DateTimeOffset)value;
            if (value is DateTime) return new DateTimeOffset((DateTime)value);
            return DateTimeOffset.MinValue;
        }

        public void Dispose()
        {
            IServer server = _server;
            _server = null;
            Dispose(server);
        }

        private static void Dispose(IServer server)
        {
            if (server == null) return;
            try { ((ISecurity)server).LogOff(); }
            catch { /* connection may already be gone */ }
            IDisposable disposable = server as IDisposable;
            if (disposable != null)
            {
                try { disposable.Dispose(); }
                catch { /* nothing useful to do while shutting down */ }
            }
        }
    }
}
