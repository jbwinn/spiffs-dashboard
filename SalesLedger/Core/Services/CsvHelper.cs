using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SalesLedger.Core.Services
{
    public static class CsvHelper
    {
        public static List<string[]> ParseCsv(string filePath)
        {
            var rows = new List<string[]>();

            if (!File.Exists(filePath))
                return rows;

            string text = File.ReadAllText(filePath, Encoding.UTF8);
            var currentField = new StringBuilder();
            var currentFields = new List<string>();
            bool inQuotes = false;

            // A quote can only open a quoted field if another quote appears later in the file.
            // Checking against the last quote's position avoids rescanning the rest of the file per quote.
            int lastQuoteIndex = text.LastIndexOf('"');

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '"')
                {
                    bool hasClosingQuote = lastQuoteIndex > i;

                    if (inQuotes)
                    {
                        // Handle escaped quotes: if we have "" inside quotes, it represents a single quote
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            currentField.Append('"');
                            i++; // skip next quote
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else if (hasClosingQuote && currentField.Length == 0)
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        currentField.Append('"');
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    currentFields.Add(CleanField(currentField));
                    currentField.Clear();
                }
                else if (c == '\n' && !inQuotes)
                {
                    currentFields.Add(CleanField(currentField));
                    currentField.Clear();

                    // Add row if it has content
                    if (currentFields.Any(f => !string.IsNullOrWhiteSpace(f)))
                    {
                        rows.Add(currentFields.ToArray());
                    }
                    currentFields.Clear();
                }
                else if (c == '\r' && !inQuotes)
                {
                    // Skip carriage return if not in quotes
                }
                else
                {
                    currentField.Append(c);
                }
            }

            // Add the last field and row if any
            if (currentField.Length > 0 || currentFields.Count > 0)
            {
                currentFields.Add(CleanField(currentField));
                if (currentFields.Any(f => !string.IsNullOrWhiteSpace(f)))
                {
                    rows.Add(currentFields.ToArray());
                }
            }

            return rows;
        }

        /// <summary>
        /// Trims surrounding whitespace and line breaks. A field made only of stray quote characters counts as empty;
        /// quotes that are part of a value (e.g. 4x4" filters) are kept.
        /// </summary>
        private static string CleanField(StringBuilder field)
        {
            var value = field.ToString().Trim(' ', '\r', '\n');
            return value.All(ch => ch == '"') ? string.Empty : value;
        }

        /// <summary>
        /// Writes rows to a CSV file, quoting fields that contain commas, quotes, line breaks or edge spaces.
        /// </summary>
        public static void WriteCsv(string filePath, IEnumerable<IReadOnlyList<string>> rows)
        {
            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                sb.AppendLine(string.Join(",", row.Select(Escape)));
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // BOM so Excel opens non-ASCII characters correctly; ParseCsv strips it on the way back in
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string Escape(string? value)
        {
            value ??= string.Empty;
            bool needsQuotes = value.IndexOfAny([',', '"', '\r', '\n']) >= 0
                               || (value.Length > 0 && (value[0] == ' ' || value[^1] == ' '));

            return needsQuotes ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }
    }
}
