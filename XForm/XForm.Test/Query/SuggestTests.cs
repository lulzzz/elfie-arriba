﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Linq;

using Microsoft.CodeAnalysis.Elfie.Model.Strings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using XForm.Query;
using XForm.Types;

namespace XForm.Test.Query
{
    [TestClass]
    public class SuggestTests
    {
        private static string s_verbs = string.Join("|", XqlParser.SupportedVerbs.OrderBy((s) => s));
        private static string s_sources = string.Join("|", SampleDatabase.XDatabaseContext.Runner.SourceNames.OrderBy((s) => s));
        private static string s_types = string.Join("|", TypeProviderFactory.SupportedTypes.OrderBy((s) => s));
        private static string s_columnNames = string.Join("|", XqlParser.EscapedColumnList(XqlParser.Parse(@"read WebRequest", null, SampleDatabase.XDatabaseContext)).OrderBy((s) => s));

        private static string s_selectListOptions = string.Join("|",
            XqlParser.EscapedColumnList(XqlParser.Parse(@"read WebRequest", null, SampleDatabase.XDatabaseContext))
            .Concat(XqlParser.EscapedFunctionList())
            .OrderBy((s) => s));

        private static string s_stringSelectListOptions = string.Join("|",
            XqlParser.EscapedColumnList(XqlParser.Parse(@"read WebRequest", null, SampleDatabase.XDatabaseContext), typeof(String8))
            .Concat(XqlParser.EscapedFunctionList(typeof(String8)))
            .OrderBy((s) => s));

        [TestMethod]
        public void Suggest_Basics()
        {
            SampleDatabase.EnsureBuilt();
            QuerySuggester suggester = new QuerySuggester(SampleDatabase.XDatabaseContext);

            // Verbs
            Assert.AreEqual(s_verbs, Values(suggester.Suggest("")));
            Assert.AreEqual(s_verbs, Values(suggester.Suggest("re")));

            // Tables
            Assert.AreEqual(s_sources, Values(suggester.Suggest("read")));

            // Valid
            Assert.AreEqual(null, Values(suggester.Suggest($"read WebRequest")));

            // Verbs (newline)
            Assert.AreEqual(s_verbs, Values(suggester.Suggest($"read WebRequest\r\n")));
            Assert.AreEqual(s_verbs, Values(suggester.Suggest($"read WebRequest\r\n ")));

            // CompareOperator
            Assert.AreEqual("!=|:|::||>|<|<=|<>|=|==|>|>=", Values(suggester.Suggest($@"
                read WebRequest
                where [HttpStatus] !")));

            // Value missing
            Assert.AreEqual(s_selectListOptions, Values(suggester.Suggest($@"
                read WebRequest
                where [HttpStatus] != ")));

            // ColumnFunctionOrLiteral
            Assert.AreEqual(s_selectListOptions, Values(suggester.Suggest($@"
                read WebRequest
                select")));

            // Function argument (type)
            Assert.AreEqual(s_types, Values(suggester.Suggest($@"
                read WebRequest
                select Trim(Cast(Cast(5, Int32), ")));

            // Function argument (ColumnFunctionOrLiteral)
            Assert.AreEqual(s_stringSelectListOptions, Values(suggester.Suggest($@"
                read WebRequest
                select Trim(")));

            // Nested Function argument (ColumnFunctionOrLiteral)
            Assert.AreEqual(s_stringSelectListOptions, Values(suggester.Suggest($@"
                read WebRequest
                select Cast(Trim(")));

            // Correct nested function use
            Assert.AreEqual(true, suggester.Suggest($@"
                read WebRequest
                select Trim(Cast(Cast(5, Int32), String8)) AS [Fiver]").IsValid);

            // Select next argument
            Assert.AreEqual(s_selectListOptions, Values(suggester.Suggest($@"
                read WebRequest
                select [HttpStatus]")));
        }

        [TestMethod]
        public void Suggest_NoErrorOnLastToken()
        {
            SampleDatabase.EnsureBuilt();
            QuerySuggester suggester = new QuerySuggester(SampleDatabase.XDatabaseContext);

            // Verify errors only when token is complete and you've moved on
            Assert.AreEqual("", suggester.Suggest("read BadTable").Context.ErrorMessage);
            Assert.AreNotEqual("", suggester.Suggest("read BadTable\r\n").Context.ErrorMessage);
            Assert.AreEqual("", suggester.Suggest("read WebRequest\r\ncase").Context.ErrorMessage);
            Assert.AreNotEqual("", suggester.Suggest("read WebRequest\r\ncase ").Context.ErrorMessage, "Bad verb 'case' is now complete");
            Assert.AreEqual("", suggester.Suggest("read WebRequest\r\ncast ").Context.ErrorMessage);
            Assert.AreEqual("", suggester.Suggest("read WebRequest\r\ncast [HttpStatur]").Context.ErrorMessage);
            Assert.AreNotEqual("", suggester.Suggest("read WebRequest\r\ncast [HttpStatur] ").Context.ErrorMessage, "Bad column name now complete");
            Assert.AreEqual("", suggester.Suggest("read WebRequest\r\ncast [HttpStatus] Int").Context.ErrorMessage);
            Assert.AreEqual("", suggester.Suggest("read WebRequest\r\ncast [HttpStatus] Int33").Context.ErrorMessage);
            Assert.AreNotEqual("", suggester.Suggest("read WebRequest\r\ncast [HttpStatus] Int33 ").Context.ErrorMessage, "Bad Type name now complete");
            Assert.AreEqual("", suggester.Suggest("read WebRequest\r\ncast [HttpStatus] Int32 ").Context.ErrorMessage, "All Valid, optional not provided");
            Assert.AreNotEqual("", suggester.Suggest("read WebRequest\r\ncast [HttpStatur] Int32").Context.ErrorMessage, "Bad column name isn't last argument");
            Assert.AreNotEqual("", suggester.Suggest("read WebRequest\r\ncase [HttpStatus] Int32").Context.ErrorMessage, "Bad verb isn't last argument");
            Assert.AreNotEqual("", suggester.Suggest("read BadTable\r\ncast [HttpStatus] Int32").Context.ErrorMessage, "Bad table isn't last argument");
        }

        [TestMethod]
        public void Suggest_FullErrorFidelity()
        {
            SampleDatabase.EnsureBuilt();
            QuerySuggester suggester = new QuerySuggester(SampleDatabase.XDatabaseContext);

            SuggestResult result = suggester.Suggest(@"
                read UsageError.WebRequest.MissingColumn");

            Assert.AreEqual(false, result.IsValid);
            Assert.AreEqual("UsageError.WebRequest.MissingColumn", result.Context.TableName);
            Assert.AreEqual(2, result.Context.QueryLineNumber);
            Assert.AreEqual("where {Expression}", result.Context.Usage);
            Assert.AreEqual("BadColumnName", result.Context.InvalidValue);
            Assert.AreEqual("[Column]", result.Context.InvalidValueCategory);
            Assert.AreEqual(s_columnNames, string.Join("|", result.Context.ValidValues));
        }

        private static string Values(SuggestResult result)
        {
            if (result.Context == null || result.Context.ValidValues == null) return null;
            return string.Join("|", result.Context.ValidValues.OrderBy((s) => s));
        }
    }
}
