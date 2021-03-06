﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;

using Microsoft.CodeAnalysis.Elfie.Model.Strings;
using Microsoft.CodeAnalysis.Elfie.Serialization;

using XForm.Data;
using XForm.Extensions;
using XForm.Http;
using XForm.IO;
using XForm.Query;

namespace XForm
{
    public class HttpService : IDisposable
    {
        private BackgroundWebServer _server;
        private XDatabaseContext _xDatabaseContext;
        private QuerySuggester _suggester;
        private static String8 s_delimiter = String8.Convert(";", new byte[1]);

        public HttpService(XDatabaseContext xDatabaseContext)
        {
            _xDatabaseContext = xDatabaseContext;
            _suggester = new QuerySuggester(_xDatabaseContext);

            _server = new BackgroundWebServer(5073, "index.html", "Web");
            _server.AddResponder("suggest", Suggest);
            _server.AddResponder("run", Run);
            _server.AddResponder("download", Download);
            _server.AddResponder("count", CountWithinTimeout);
            _server.AddResponder("save", Save);
            _server.AddResponder("test", Test);
        }

        public void Run()
        {
            using (_server)
            {
                _server.Run();
            }

            _server = null;
        }

        private void Test(IHttpRequest request, IHttpResponse response)
        {
            using (StreamWriter writer = new StreamWriter(response.OutputStream))
            {
                writer.WriteLine(request.Url);
                writer.WriteLine(request.User.Identity.Name);
            }

            response.Close();
        }

        public void HandleRequest(IHttpRequest request, IHttpResponse response)
        {
            _server.HandleRequest(request, response);
        }

        private void Suggest(IHttpRequest request, IHttpResponse response)
        {
            using (ITabularWriter writer = WriterForFormat("json", response))
            {
                try
                {
                    string query = Require(request, "q");

                    DateTime asOfDate = ParseOrDefault(request.QueryString["asof"], _xDatabaseContext.RequestedAsOfDateTime);
                    SuggestResult result = _suggester.Suggest(query, asOfDate);

                    // If the query is valid and there are no extra values valid next, just return valid
                    if (result.IsValid == true && result.Context == null)
                    {
                        writer.SetColumns(new string[] { "Valid" });
                        writer.Write(true);
                        writer.NextRow();
                    }
                    else
                    {
                        WriteException(result, writer);
                    }
                }
                catch (Exception ex)
                {
                    WriteException(ex, writer, false);
                }
            }
        }

        private void Run(IHttpRequest request, IHttpResponse response)
        {
            try
            {
                Run(
                    Require(request, "q"),
                    request.QueryString["fmt"] ?? "json",
                    ParseOrDefault(request.QueryString["rowLimit"], 100),
                    ParseOrDefault(request.QueryString["colLimit"], -1),
                    ParseOrDefault(request.QueryString["asof"], _xDatabaseContext.RequestedAsOfDateTime),
                    response);
            }
            catch (Exception ex)
            {
                using (ITabularWriter writer = WriterForFormat("json", response))
                {
                    WriteException(ex, writer);
                }
            }
        }

        private void Download(IHttpRequest request, IHttpResponse response)
        {
            try
            {
                Run(
                    Require(request, "q"),
                    Require(request, "fmt"),
                    ParseOrDefault(request.QueryString["rowLimit"], -1),
                    ParseOrDefault(request.QueryString["colLimit"], -1),
                    ParseOrDefault(request.QueryString["asof"], _xDatabaseContext.RequestedAsOfDateTime),
                    response);
            }
            catch (Exception ex)
            {
                using (ITabularWriter writer = WriterForFormat("json", response))
                {
                    WriteException(ex, writer);
                }
            }
        }

        private void CountWithinTimeout(IHttpRequest request, IHttpResponse response)
        {
            try
            {
                CountWithinTimeout(
                    Require(request, "q"),
                    TimeSpan.FromMilliseconds(ParseOrDefault(request.QueryString["ms"], 5000)),
                    ParseOrDefault(request.QueryString["asof"], _xDatabaseContext.RequestedAsOfDateTime),
                    response);
            }
            catch (Exception ex)
            {
                using (ITabularWriter writer = WriterForFormat("json", response))
                {
                    WriteException(ex, writer);
                }
            }
        }

        private void CountWithinTimeout(string query, TimeSpan timeout, DateTime asOfDate, IHttpResponse response)
        {
            IXTable pipeline = null;

            try
            {
                XDatabaseContext context = _xDatabaseContext;

                // Build for another moment in time if requested
                if (asOfDate != _xDatabaseContext.RequestedAsOfDateTime)
                {
                    context = new XDatabaseContext(_xDatabaseContext) { RequestedAsOfDateTime = asOfDate };
                }

                // Build a Pipeline for the Query
                pipeline = context.Query(query);

                // Try to get the count up to the timeout
                if (Debugger.IsAttached) timeout = TimeSpan.MaxValue;
                RunResult result = pipeline.RunUntilTimeout(timeout);

                using (ITabularWriter writer = WriterForFormat("json", response))
                {
                    writer.SetColumns(new string[] { "Count", "IsComplete", "RuntimeMs" });
                    writer.Write((int)result.RowCount);
                    writer.Write(result.IsComplete);
                    writer.Write((int)result.Elapsed.TotalMilliseconds);
                    writer.NextRow();
                }
            }
            finally
            {
                if (pipeline != null)
                {
                    pipeline.Dispose();
                    pipeline = null;
                }
            }
        }

        private void Run(string query, string format, int rowCountLimit, int colCountLimit, DateTime asOfDate, IHttpResponse response)
        {
            IXTable pipeline = null;

            try
            {
                XDatabaseContext context = _xDatabaseContext;

                // Build for another moment in time if requested
                if (asOfDate != _xDatabaseContext.RequestedAsOfDateTime)
                {
                    context = new XDatabaseContext(_xDatabaseContext) { RequestedAsOfDateTime = asOfDate };
                }

                // Build a Pipeline for the Query
                pipeline = context.Query(query);

                // Restrict the row and column count if requested
                if (rowCountLimit >= 0 || colCountLimit > 0)
                {
                    pipeline = new Verbs.Limit(pipeline, rowCountLimit, colCountLimit);
                }

                // Build a writer for the desired format
                using (ITabularWriter writer = WriterForFormat(format, response))
                {
                    // Run the query and return the output
                    pipeline = new TabularFileWriter(pipeline, writer);
                    pipeline.RunAndDispose();
                }
            }
            finally
            {
                if (pipeline != null)
                {
                    pipeline.Dispose();
                    pipeline = null;
                }
            }
        }

        private void Save(IHttpRequest context, IHttpResponse response)
        {
            try
            {
                Save(
                    Require(context, "q"),
                    Require(context, "name"));

                // Success
                response.StatusCode = 200;
            }
            catch (Exception ex)
            {
                using (ITabularWriter writer = WriterForFormat("json", response))
                {
                    WriteException(ex, writer);
                }
            }
        }

        private void Save(string query, string tableName)
        {
            _xDatabaseContext.Runner.Save(query, tableName);
        }

        private ITabularWriter WriterForFormat(string format, IHttpResponse response)
        {
            Stream toStream = response.OutputStream;
            toStream = new BufferedStream(toStream, 64 * 1024);

            switch (format.ToLowerInvariant())
            {
                case "json":
                    return new JsonTabularWriter(toStream);
                case "csv":
                    response.AddHeader("Content-Disposition", "attachment; filename=\"Result.csv\"");
                    return new CsvWriter(toStream);
                case "tsv":
                    response.AddHeader("Content-Disposition", "attachment; filename=\"Result.tsv\"");
                    return new TsvWriter(toStream);
                default:
                    throw new ArgumentException("fmt");
            }
        }

        private void WriteException(SuggestResult result, ITabularWriter writer)
        {
            WriteException(result.Context, result.IsValid, writer);
        }

        private void WriteException(Exception ex, ITabularWriter writer, bool isValid = false)
        {
            String8Block block = new String8Block();

            if (ex is UsageException)
            {
                UsageException ue = ex as UsageException;
                WriteException(ue.Context, isValid, writer);
            }
            else
            {
                writer.SetColumns(new string[] { "Valid", "Message", "Stack" });
                writer.Write(false);
                writer.Write(block.GetCopy(ex.Message));
                writer.Write(block.GetCopy(ex.StackTrace));
                writer.NextRow();
            }
        }

        private void WriteException(ErrorContext context, bool isValid, ITabularWriter writer)
        {
            String8Block block = new String8Block();

            writer.SetColumns(new string[] { "Valid", "Usage", "ItemCategory", "ErrorMessage", "Values", "InvalidToken", "InvalidTokenIndex" });
            writer.Write(isValid);
            writer.Write(block.GetCopy(context.Usage));
            writer.Write(block.GetCopy(context.InvalidValueCategory));
            writer.Write(block.GetCopy(context.ErrorMessage));

            String8 values = String8.Empty;
            if (context.ValidValues != null)
            {
                foreach (string value in context.ValidValues)
                {
                    values = block.Concatenate(values, s_delimiter, block.GetCopy(value));
                }
            }
            writer.Write(values);

            writer.Write(block.GetCopy(context.InvalidValue ?? ""));
            writer.Write(context.InvalidTokenIndex);

            writer.NextRow();
        }

        private static string Require(IHttpRequest context, string parameterName)
        {
            string value = context.QueryString[parameterName];
            if (value == null) throw new ArgumentException($"Request must include parameter \"{parameterName}\".");
            return value;
        }

        private static int ParseOrDefault(string value, int defaultValue)
        {
            if (String.IsNullOrEmpty(value)) return defaultValue;

            int result;
            if (!int.TryParse(value, out result)) return defaultValue;

            return result;
        }

        private static DateTime ParseOrDefault(string value, DateTime defaultValue)
        {
            if (String.IsNullOrEmpty(value)) return defaultValue;

            DateTime result;
            if (!DateTime.TryParse(value, out result)) return defaultValue;

            return result;
        }

        public void Dispose()
        {
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
        }
    }
}
