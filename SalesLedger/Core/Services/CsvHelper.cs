using System;
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

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c == '"')
                {
                    // Check if there is a matching quote later in the file
                    bool hasClosingQuote = false;
                    for (int j = i + 1; j < text.Length; j++)
                    {
                        if (text[j] == '"')
                        {
                            hasClosingQuote = true;
                            break;
                        }
                    }

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
                    currentFields.Add(currentField.ToString().Trim(' ', '"', '\r', '\n'));
                    currentField.Clear();
                }
                else if (c == '\n' && !inQuotes)
                {
                    currentFields.Add(currentField.ToString().Trim(' ', '"', '\r', '\n'));
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
                currentFields.Add(currentField.ToString().Trim(' ', '"', '\r', '\n'));
                if (currentFields.Any(f => !string.IsNullOrWhiteSpace(f)))
                {
                    rows.Add(currentFields.ToArray());
                }
            }

            return rows;
        }
    }
}
