using System.Runtime.CompilerServices;
using Dapper;

namespace WorkState.Core.Queries;

/// <summary>
/// Dapper's type handlers are process-global. Registered from a query class's static constructor,
/// they existed only once that class had been touched, so a mint as a restarted server's first
/// request (<see cref="Writing.SessionStore"/> reads a <c>timestamptz</c> as
/// <see cref="DateTimeOffset"/>) threw before any query class had run. The module initializer runs
/// before any code in this assembly does.
/// </summary>
internal static class DapperHandlers
{
#pragma warning disable CA2255 // process-global state that must exist before any caller's first query
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        SqlMapper.AddTypeHandler(new BoardQueries.TimestampHandler());
        SqlMapper.AddTypeHandler(new WorkStateQueries.TextArrayHandler());
    }
}
