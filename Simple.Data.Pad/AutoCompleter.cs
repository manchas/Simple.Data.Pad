﻿namespace Simple.Data.Pad
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Ado;
    using Ado.Schema;
    using Interop;

    public class AutoCompleter
    {
        private static readonly string[] Empty = new string[0];
        private readonly ISchemaProvider _schemaProvider;
        private readonly ConcurrentDictionary<string,string[]> _cache = new ConcurrentDictionary<string, string[]>();
        private static readonly Prettifier Prettifier = new Prettifier();

        public AutoCompleter(DataStrategy database)
        {
            if (database != null)
            {
                var adoAdapter = database.GetAdapter() as AdoAdapter;
                if (adoAdapter != null)
                {
                    _schemaProvider = adoAdapter.SchemaProvider;
                }
            }
        }

        public AutoCompleter(ISchemaProvider schemaProvider)
        {
            _schemaProvider = schemaProvider;
        }

        public IEnumerable<string> GetOptions(string currentText)
        {
            // Check that we can even do anything useful
            if (_schemaProvider == null || string.IsNullOrWhiteSpace(currentText) || (!currentText.Contains("."))) return Empty;

            var array = _cache.GetOrAdd(currentText, GetOptionsImpl);

            // If the only thing in the array is the last identifier before the cursor, then we've got nothing to say
            if (array.Length == 1 && currentText.Substring(currentText.LastIndexOf('.') + 1).Equals(array[0], StringComparison.CurrentCultureIgnoreCase))
                return Empty;

            return array;
        }

        private string[] GetOptionsImpl(string currentText)
        {
            var tokens = new Lexer(currentText).GetTokens().ToArray();
            if (tokens.Length < 2) return Empty;
            var db = tokens[0].Value;
            int current = tokens.Length - 1;

            int openMethodIndex;
            if ((tokens[current].Type == TokenType.OpenParen || tokens[current].Type == TokenType.Comma
                || tokens[current - 1].Type == TokenType.OpenParen || tokens[current - 1].Type == TokenType.Comma)
                && IsInCallWhichTakesNamedParameters(current, tokens, out openMethodIndex))
            {
                return GetNamedParameterOptions(tokens, openMethodIndex, current);
            }

            if (tokens[current].Type != TokenType.Dot && tokens[current].Type != TokenType.Identifier) return Empty;

            // Is the user halfway through typing an identifier?
            string partial = string.Empty;
            if (tokens[current].Type == TokenType.Identifier)
            {
                partial = tokens[current].Value.ToString();
                --current;
            }

            // Skip over what should be a dot.
            if (tokens[current].Type == TokenType.Dot)
            {
                --current;
            }

            // Is the thing before the dot a method call?
            if (tokens[current].Type == TokenType.CloseParen)
            {
                return GetOptionsForMethodReturnType(tokens, current, false, partial).ToArray();
            }

            // Now we should be on an identifier
            if (tokens[current].Type != TokenType.Identifier)
            {
                return Empty;
            }

            var array = GetOptionsImpl(partial, tokens[current], db).ToArray();
            if (array.Length == 1 && array[0].Equals(partial, StringComparison.CurrentCultureIgnoreCase)) return Empty;
            return array;
        }

        private string[] GetNamedParameterOptions(Token[] tokens, int openMethodIndex, int currentIndex)
        {
            string tableName = tokens[openMethodIndex - 2].Value.ToString();
            Table table = _schemaProvider.GetTables()
                .Where(t => Prettifier.Prettify(t.ActualName) == Prettifier.Prettify(tableName))
                .SingleOrDefault();
            if (table == null) return Empty;
            var columnQuery = _schemaProvider.GetColumns(table).Select(c => Prettifier.Prettify(c.ActualName) + ":");

            if (currentIndex - openMethodIndex > 2)
            {
                var usedParameterNames = new HashSet<string>();
                for (int i = openMethodIndex + 1; i < currentIndex; i++)
                {
                    if (tokens[i].Type == TokenType.Identifier && tokens[i+1].Type == TokenType.Colon)
                    {
                        usedParameterNames.Add(tokens[i].Value + ":");
                        ++i;
                    }
                }

                if (usedParameterNames.Count > 0)
                {
                    columnQuery = columnQuery.Where(s => !usedParameterNames.Contains(s));
                }
            }

            return columnQuery.ToArray();
        }

        private static int FindUnmatchedOpenParen(Token[] tokens, int current)
        {
            int closeParenCount = 0;
            for (int index = current; index > 1; index--)
            {
                if (tokens[index].Type == TokenType.CloseParen)
                {
                    ++closeParenCount;
                }
                else if (tokens[index].Type == TokenType.OpenParen)
                {
                    --closeParenCount;
                    if (closeParenCount < 1)
                    {
                        return index;
                    }
                }
            }

            return -1;
        }

        private static bool IsInCallWhichTakesNamedParameters(int current, Token[] tokens, out int openMethodIndex)
        {
            int openCallIndex = FindUnmatchedOpenParen(tokens, current);
            if (openCallIndex > 0)
            {
                if (IsCallWhichTakesNamedParameters(tokens[openCallIndex], tokens[openCallIndex - 1]))
                {
                    openMethodIndex = openCallIndex - 1;
                    return true;
                }
            }
            openMethodIndex = -1;
            return false;
        }

        private static bool IsCallWhichTakesNamedParameters(Token current, Token previous)
        {
            return current.Type == TokenType.OpenParen
                   && previous.Type == TokenType.Identifier
                   && (previous.Value.ToString().Equals("FindBy", StringComparison.CurrentCultureIgnoreCase)
                   || previous.Value.ToString().Equals("FindAllBy", StringComparison.CurrentCultureIgnoreCase)
                   || previous.Value.ToString().Equals("Insert", StringComparison.CurrentCultureIgnoreCase)
                   || previous.Value.ToString().StartsWith("DeleteBy", StringComparison.CurrentCultureIgnoreCase)
                   || previous.Value.ToString().StartsWith("UpdateBy", StringComparison.CurrentCultureIgnoreCase)
                   );
        }

        private IEnumerable<string> GetOptionsForMethodReturnType(Token[] tokens, int current, bool methodChainIncludesOrderBy, string partial)
        {
            current = Lexer.FindIndexOfOpeningToken(tokens, current, TokenType.OpenParen);
            if (--current < 0)
            {
                return Empty;
            }

            if (tokens[current].Type != TokenType.Identifier) return Empty;

            if (IsAMethodThatReturnsAQuery(tokens[current].Value.ToString()))
            {
                current -= 2;
                if (tokens[current].Type != TokenType.Identifier)
                {
                    return Empty;
                }
                var options = QueryOptions(tokens[current].Value.ToString(), methodChainIncludesOrderBy);
                if (!string.IsNullOrWhiteSpace(partial))
                {
                    options = options.Where(s => s.StartsWith(partial, StringComparison.CurrentCultureIgnoreCase));
                }
                return options;
            }

            string methodName = tokens[current].Value.ToString();
            if (methodName.Equals("join", StringComparison.CurrentCultureIgnoreCase)) return new[] { "On" };

            methodChainIncludesOrderBy = methodChainIncludesOrderBy || methodName.StartsWith("OrderBy", StringComparison.CurrentCultureIgnoreCase);

            --current;

            if (tokens[current].Type == TokenType.Dot)
            {
                --current;
            }

            if (tokens[current].Type == TokenType.CloseParen)
            {
                return GetOptionsForMethodReturnType(tokens, current, methodChainIncludesOrderBy, partial);
            }

            return Empty;
        }

        private static bool IsAMethodThatReturnsAQuery(string identifier)
        {
            return identifier.StartsWith("FindAll", StringComparison.CurrentCultureIgnoreCase)
                   || identifier.Equals("All", StringComparison.CurrentCultureIgnoreCase)
                   || identifier.StartsWith("Query", StringComparison.CurrentCultureIgnoreCase);
        }

        private IEnumerable<string> GetOptionsImpl(string partial, Token token, object db)
        {
            if (token.Value == db)
            {
                return DatabaseOptions()
                    .Select(Prettifier.Prettify)
                    .Where(s => s.StartsWith(partial, StringComparison.CurrentCultureIgnoreCase))
                    .OrderBy(s => s);
            }

            return TableOptions(token.Value.ToString())
                .Where(s => s.StartsWith(partial, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(s => s);
        }

        private IEnumerable<string> TableOptions(string tableName)
        {
            Table table = _schemaProvider.GetTables()
                .Where(t => Prettifier.Prettify(t.ActualName) == Prettifier.Prettify(tableName))
                .SingleOrDefault();

            if (table == null) yield break;

            yield return "All";
            yield return "Query";
            yield return "FindBy";
            yield return "FindAllBy";

            foreach (var column in _schemaProvider.GetColumns(table).Select(c => Prettifier.Prettify(c.ActualName)))
            {
                yield return column;
                yield return "FindBy" + column;
                yield return "FindAllBy" + column;
            }
        }

        private IEnumerable<string> QueryOptions(string tableName, bool includeThenBy)
        {
            yield return "Select";
            yield return "Where";
            yield return "ReplaceWhere";
            if (includeThenBy)
            {
                yield return "ThenBy";
                yield return "ThenByDescending";
            }
            else
            {
                yield return "OrderBy";
                yield return "OrderByDescending";
            }
            yield return "Skip";
            yield return "Take";
            yield return "Join";

            Table table = _schemaProvider.GetTables()
                .Where(t => Prettifier.Prettify(t.ActualName) == Prettifier.Prettify(tableName))
                .SingleOrDefault();

            if (table == null) yield break;
            foreach (var column in _schemaProvider.GetColumns(table).Select(c => Prettifier.Prettify(c.ActualName)))
            {
                if (includeThenBy)
                {
                    yield return "ThenBy" + column;
                    yield return "ThenBy" + column + "Descending";
                }
                else
                {
                    yield return "OrderBy" + column;
                    yield return "OrderBy" + column + "Descending";
                }
            }
        }

        private IEnumerable<string> DatabaseOptions()
        {
            foreach (var table in _schemaProvider.GetTables())
            {
                yield return table.ActualName;
            }

            foreach (var storedProcedure in _schemaProvider.GetStoredProcedures())
            {
                yield return storedProcedure.Name;
            }
        }

    }
}