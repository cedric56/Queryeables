using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Common;
using System.Text;

namespace Avae.DAL;

public static class Extensions
{
    public static void UseFactory<TDBConnection>(this IServiceCollection services,
        string connectionString,
        bool logCommands = false,
        Func<DBFactoryOptions, ILogger?, DbProviderFactory>? factory = null,
        LogLevel commandLogLevel = LogLevel.Information)
        where TDBConnection : DbConnection, new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new DBFactoryOptions
        {
            LogCommands = logCommands,
            CommandLogLevel = commandLogLevel
        };

        services.AddSingleton(options);
        services.AddSingleton<DbProviderFactory>(sp =>
        {
            ILogger? logger = null;
            if (options.LogCommands)
            {
                var loggerFactory = sp.GetService<ILoggerFactory>();
                logger = loggerFactory?.CreateLogger("Avae.DAL.SQL");
            }
            return factory is not null ?
            factory(options, logger) :
            new DBFactory<TDBConnection>(connectionString, options, logger);
        });
        services.AddSingleton<IDBSession>(sp => (IDBSession)sp.GetRequiredService<DbProviderFactory>());
    }

    internal static string ReplaceWholeWord(this string s, string word, string bywhat)
    {
        char firstLetter = word[0];
        var sb = new StringBuilder();
        bool previousWasLetterOrDigit = false;
        int i = 0;
        while (i < s.Length - word.Length + 1)
        {
            bool wordFound = false;
            char c = s[i];
            if (c == firstLetter)
                if (!previousWasLetterOrDigit)
                    if (s.Substring(i, word.Length).Equals(word))
                    {
                        wordFound = true;
                        bool wholeWordFound = true;
                        if (s.Length > i + word.Length)
                        {
                            if (char.IsLetterOrDigit(s[i + word.Length]))
                                wholeWordFound = false;
                        }

                        if (wholeWordFound)
                            sb.Append(bywhat);
                        else
                            sb.Append(word);

                        i += word.Length;
                    }

            if (!wordFound)
            {
                previousWasLetterOrDigit = char.IsLetterOrDigit(c);
                sb.Append(c);
                i++;
            }
        }

        if (s.Length - i > 0)
            sb.Append(s.AsSpan(i));

        return sb.ToString();
    }
}
