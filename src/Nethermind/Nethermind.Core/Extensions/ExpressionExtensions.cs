// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Nethermind.Core.Extensions
{
    public static class ExpressionExtensions
    {
        public static string GetName<T, TProperty>(this Expression<Func<T, TProperty>> action) => GetMemberInfo(action).Member.Name;

        public static MemberExpression GetMemberInfo(this Expression method)
        {
            LambdaExpression lambda = method as LambdaExpression ?? throw new ArgumentException($"Only {typeof(LambdaExpression)} are supported", nameof(method));

            MemberExpression? memberExpr = lambda.Body.NodeType switch
            {
                ExpressionType.Convert => ((UnaryExpression)lambda.Body).Operand as MemberExpression,
                ExpressionType.MemberAccess => lambda.Body as MemberExpression,
                _ => throw new ArgumentException($"Only {typeof(LambdaExpression)} too complex", nameof(method))
            };

            return memberExpr!;
        }

        /// <summary>
        /// Convert a lambda expression for a getter into a setter
        /// </summary>
        public static Action<T, TProperty> GetSetter<T, TProperty>(this Expression<Func<T, TProperty>> expression)
        {
            var memberExpression = expression.GetMemberInfo();
            if (memberExpression.Member is PropertyInfo property)
            {
                var setMethod = property.GetSetMethod();

                if (setMethod is null)
                {
                    throw new NotSupportedException($"Property {typeof(T).Name}{memberExpression.Member.Name} doesn't have a setter.");
                }

                var parameterT = Expression.Parameter(typeof(T), "x");
                var parameterTProperty = Expression.Parameter(typeof(TProperty), "y");

                var newExpression =
                    Expression.Lambda<Action<T, TProperty>>(
                        Expression.Call(parameterT, setMethod, parameterTProperty),
                        parameterT,
                        parameterTProperty
                    );

                return newExpression.Compile();
            }
            else
            {
                // TODO: Add fields
                throw new NotSupportedException($"Member {typeof(T).Name}{memberExpression.Member.Name} is not a property.");
            }
        }
    }
}
