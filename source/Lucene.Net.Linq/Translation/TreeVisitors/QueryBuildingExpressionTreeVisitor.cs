﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Lucene.Net.Linq.Clauses.Expressions;
using Lucene.Net.Linq.Clauses.TreeVisitors;
using Lucene.Net.Linq.Mapping;
using Lucene.Net.Linq.Search;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;

namespace Lucene.Net.Linq.Translation.TreeVisitors
{
    internal class QueryBuildingExpressionTreeVisitor : LuceneExpressionTreeVisitor
    {
        private readonly IFieldMappingInfoProvider fieldMappingInfoProvider;
        private readonly Stack<Query> queries = new Stack<Query>();

        internal QueryBuildingExpressionTreeVisitor(IFieldMappingInfoProvider fieldMappingInfoProvider)
        {
            this.fieldMappingInfoProvider = fieldMappingInfoProvider;
        }

        public Query Query
        {
            get
            {
                if (queries.Count == 0) return new MatchAllDocsQuery();
                var query = queries.Peek();
                if (query is BooleanQuery)
                {
                    var booleanQuery = (BooleanQuery)query.Clone();
                    if (booleanQuery.GetClauses().All(c => c.Occur == Occur.MUST_NOT))
                    {
                        booleanQuery.Add(new MatchAllDocsQuery(), Occur.SHOULD);
                        return booleanQuery;
                    }
                }
                return query;
            }
        }

        protected override Expression VisitBinaryExpression(BinaryExpression expression)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.AndAlso:
                case ExpressionType.OrElse:
                    return MakeBooleanQuery(expression);
                default:
                    throw new NotSupportedException("BinaryExpression of type " + expression.NodeType + " is not supported.");
            }
        }

        protected override Expression VisitBoostBinaryExpression(BoostBinaryExpression expression)
        {
            var result = base.VisitBoostBinaryExpression(expression);

            var query = queries.Peek();

            query.Boost = expression.Boost;

            return result;
        }

        protected override Expression VisitLuceneQueryExpression(LuceneQueryExpression expression)
        {
            queries.Push(expression.Query);
            return expression;
        }

        protected override Expression VisitLuceneQueryPredicateExpression(LuceneQueryPredicateExpression expression)
        {
            if (expression.QueryField is LuceneQueryAnyFieldExpression)
            {
                AddMultiFieldQuery(expression);

                return base.VisitLuceneQueryPredicateExpression(expression);
            }

            var mapping = fieldMappingInfoProvider.GetMappingInfo(expression.QueryField.FieldName);

            var pattern = GetPattern(expression, mapping);

            var occur = expression.Occur;
            
            if (string.IsNullOrEmpty(pattern))
            {
                pattern = "*";
                occur = Negate(occur);
            }

            Query query;

            if (expression.QueryType == QueryType.GreaterThan || expression.QueryType == QueryType.GreaterThanOrEqual)
            {
                query = CreateRangeQuery(mapping, expression.QueryType, expression, null);
            }
            else if (expression.QueryType == QueryType.LessThan || expression.QueryType == QueryType.LessThanOrEqual)
            {
                query = CreateRangeQuery(mapping, expression.QueryType, null, expression);
            }
            else
            {
                query = mapping.CreateQuery(pattern);
            }

            var booleanQuery = new BooleanQuery();

            query.Boost = expression.Boost;
            booleanQuery.Add(query, occur);

            queries.Push(booleanQuery);

            return base.VisitLuceneQueryPredicateExpression(expression);
        }

        private string GetPattern(LuceneQueryPredicateExpression expression, IFieldMappingInfo mapping)
        {
            var pattern = EvaluateExpressionToString(expression, mapping);

            switch (expression.QueryType)
            {
                case QueryType.Prefix:
                    pattern += "*";
                    break;
                case QueryType.Wildcard:
                    pattern = "*" + pattern + "*";
                    break;
                case QueryType.Suffix:
                    pattern = "*" + pattern;
                    break;
            }
            return pattern;
        }

        private void AddMultiFieldQuery(LuceneQueryPredicateExpression expression)
        {
            var query = new BooleanQuery();
            query.Add(new BooleanClause(fieldMappingInfoProvider.CreateMultiFieldQuery(GetPattern(expression, null)), expression.Occur));
            queries.Push(query);
        }

        private Query CreateRangeQuery(IFieldMappingInfo mapping, QueryType queryType, LuceneQueryPredicateExpression lowerBoundExpression, LuceneQueryPredicateExpression upperBoundExpression)
        {
            var lowerBound = lowerBoundExpression == null ? null : EvaluateExpression(lowerBoundExpression);
            var upperBound = upperBoundExpression == null ? null : EvaluateExpression(upperBoundExpression);

            var lowerRange = RangeType.Inclusive;
            var upperRange = (queryType == QueryType.LessThan || queryType == QueryType.GreaterThan) ? RangeType.Exclusive : RangeType.Inclusive;

            if (upperBoundExpression == null)
            {
                lowerRange = upperRange;
                upperRange = RangeType.Inclusive;
            }

            return mapping.CreateRangeQuery(lowerBound, upperBound, lowerRange, upperRange);
        }

        private static Occur Negate(Occur occur)
        {
            return (occur == Occur.MUST_NOT)
                       ? Occur.MUST
                       : Occur.MUST_NOT;
        }

        private Expression MakeBooleanQuery(BinaryExpression expression)
        {
            var result = base.VisitBinaryExpression(expression);

            var second = (BooleanQuery)queries.Pop();
            var first = (BooleanQuery)queries.Pop();
            var occur = expression.NodeType == ExpressionType.AndAlso ? Occur.MUST : Occur.SHOULD;

            var query = new BooleanQuery();
            Combine(query, first, occur);
            Combine(query, second, occur);

            queries.Push(query);

            return result;
        }

        private void Combine(BooleanQuery target, BooleanQuery source, Occur occur)
        {
            if (source.GetClauses().Length == 1)
            {
                var clause = source.GetClauses()[0];
                if (clause.Occur == Occur.MUST)
                {
                    clause.Occur = occur;
                }
                target.Add(clause);
            }
            else
            {
                target.Add(source, occur);
            }
        }

        private object EvaluateExpression(LuceneQueryPredicateExpression expression)
        {
            var lambda = Expression.Lambda(expression.QueryPattern).Compile();
            return lambda.DynamicInvoke();
        }

        private string EvaluateExpressionToString(LuceneQueryPredicateExpression expression, IFieldMappingInfo mapping)
        {
            var result = EvaluateExpression(expression);
            
            var str = mapping == null ? result.ToString() : mapping.ConvertToQueryExpression(result);

            if (expression.AllowSpecialCharacters)
                return str;

            return mapping != null ? mapping.EscapeSpecialCharacters(str) : str;
        }
    }
}