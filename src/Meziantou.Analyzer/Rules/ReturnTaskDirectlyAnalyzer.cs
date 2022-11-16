using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Meziantou.Analyzer.Internals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Meziantou.Analyzer.Rules;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReturnTaskDirectlyAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        RuleIdentifiers.ReturnTaskDirectly,
        title: "Return Task instead of awaiting it",
        messageFormat: "Return Task instead of awaiting it",
        RuleCategories.Performance,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Returning a Task directly instead of awaiting it increases performance, as the compiler does not need to create an additional state machine.",
        helpLinkUri: RuleIdentifiers.GetHelpUri(RuleIdentifiers.ReturnTaskDirectly));

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var analyzerContext = new AnalyzerContext(context.Compilation);
        context.RegisterSyntaxNodeAction(ctx => analyzerContext.OnMethodAnalysis(ctx), SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement, SyntaxKind.ParenthesizedLambdaExpression);
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(s_rule);

    private sealed class AnalyzerContext
    {
        public AnalyzerContext(Compilation compilation)
        {
            TaskSymbol = compilation.GetBestTypeByMetadataName("System.Threading.Tasks.Task");
            TaskOfTSymbol = compilation.GetBestTypeByMetadataName("System.Threading.Tasks.Task`1");
            ValueTaskSymbol = compilation.GetBestTypeByMetadataName("System.Threading.Tasks.ValueTask");
            ValueTaskOfTSymbol = compilation.GetBestTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        }

        public INamedTypeSymbol? TaskSymbol { get; }
        public INamedTypeSymbol? TaskOfTSymbol { get; }
        public INamedTypeSymbol? ValueTaskSymbol { get; }
        public INamedTypeSymbol? ValueTaskOfTSymbol { get; }

        public void OnMethodAnalysis(SyntaxNodeAnalysisContext context)
        {
            var node = context.Node;

            static bool IsMethodValid(SyntaxNode node, [NotNullWhen(true)] IMethodSymbol? methodSymbol)
            {
                return methodSymbol is not null
                       && methodSymbol.IsAsync
                       && !methodSymbol.ReturnsVoid
                       && node.DescendantNodes().Any(n => n.IsKind(SyntaxKind.AwaitExpression));
            }

            BlockSyntax? body;
            ExpressionSyntax? expressionBody;
            ITypeSymbol returnTypeSymbol;
            if (node.IsKind(SyntaxKind.MethodDeclaration) || node.IsKind(SyntaxKind.LocalFunctionStatement))
            {
                var methodSymbol = (IMethodSymbol?)context.SemanticModel.GetDeclaredSymbol(node);
                if (!IsMethodValid(node, methodSymbol))
                {
                    return;
                }

                body = (node as MethodDeclarationSyntax)?.Body ?? (node as LocalFunctionStatementSyntax)?.Body;
                expressionBody = (node as MethodDeclarationSyntax)?.ExpressionBody?.Expression ?? (node as LocalFunctionStatementSyntax)?.ExpressionBody?.Expression;
                returnTypeSymbol = methodSymbol.ReturnType;
            }
            else if (context.Node.IsKind(SyntaxKind.ParenthesizedLambdaExpression))
            {
                var lambdaSymbol = (IMethodSymbol?)context.SemanticModel.GetSymbolInfo(node, cancellationToken: context.CancellationToken).Symbol;
                if (!IsMethodValid(node, lambdaSymbol))
                {
                    return;
                }

                body = ((ParenthesizedLambdaExpressionSyntax)node).Block;
                expressionBody = ((ParenthesizedLambdaExpressionSyntax)node).ExpressionBody;
                returnTypeSymbol = lambdaSymbol.ReturnType;
            }
            else
            {
                return;
            }

            var semanticModel = context.SemanticModel;
            if (expressionBody is not null && TryGetDiagnosticForExpressionBody(semanticModel, expressionBody, returnTypeSymbol, out var diagnostic))
            {
                context.ReportDiagnostic(diagnostic);

                return;
            }

            if ((returnTypeSymbol.Equals(TaskSymbol, SymbolEqualityComparer.Default)
                 || returnTypeSymbol.Equals(ValueTaskSymbol, SymbolEqualityComparer.Default))
                && body is not null
                && TryGetDiagnosticForTaskReturn(semanticModel, body, returnTypeSymbol, out diagnostic))
            {
                context.ReportDiagnostic(diagnostic);

                return;
            }

            if ((returnTypeSymbol.OriginalDefinition.Equals(TaskOfTSymbol, SymbolEqualityComparer.Default)
                 || returnTypeSymbol.OriginalDefinition.Equals(ValueTaskOfTSymbol, SymbolEqualityComparer.Default))
                && body is not null
                && TryGetDiagnosticForTaskWithValueReturn(semanticModel, body, returnTypeSymbol, out diagnostic))
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        private static bool TryGetDiagnosticForExpressionBody(SemanticModel semanticModel, ExpressionSyntax expressionBody, ITypeSymbol returnTypeSymbol,
            [NotNullWhen(true)] out Diagnostic? diagnostic)
        {
            diagnostic = null;
            if (expressionBody is AwaitExpressionSyntax awaitExpression && !HasCovariantReturn(semanticModel, awaitExpression, returnTypeSymbol))
            {
                diagnostic = Diagnostic.Create(s_rule, expressionBody.GetLocation());

                return true;
            }

            return false;
        }

        private static bool TryGetDiagnosticForTaskReturn(SemanticModel semanticModel, BlockSyntax methodBody, ITypeSymbol returnTypeSymbol, [NotNullWhen(true)] out Diagnostic? diagnostic)
        {
            diagnostic = null;
            if (methodBody.ContainsUsingStatement())
            {
                return false;
            }

            bool IsAwaitCandidateForOptimization(AwaitExpressionSyntax awaitExpression)
            {
                return (awaitExpression.IsNextStatementReturnStatement()
                        || methodBody.Statements.Last() is ExpressionStatementSyntax expressionStatement && expressionStatement.Expression.Equals(awaitExpression))
                       && !HasCovariantReturn(semanticModel, awaitExpression, returnTypeSymbol)
                       && !awaitExpression.HasParent(SyntaxKind.TryStatement)
                       && !awaitExpression.HasParent(SyntaxKind.UsingStatement)
                       && !(awaitExpression.Parent?.Parent is BlockSyntax block && block.ContainsUsingStatement());
            }

            var awaitExpressions = methodBody.DescendantNodes(node => !node.IsKind(SyntaxKind.LocalFunctionStatement))
                .OfType<AwaitExpressionSyntax>()
                .ToList();
            if (awaitExpressions.All(IsAwaitCandidateForOptimization))
            {
                var additionalLocations = awaitExpressions.Skip(1).Select(a => a.GetLocation());
                diagnostic = Diagnostic.Create(s_rule, awaitExpressions[0].GetLocation(), additionalLocations);

                return true;
            }

            return false;
        }

        private static bool TryGetDiagnosticForTaskWithValueReturn(SemanticModel semanticModel, BlockSyntax methodBody, ITypeSymbol returnTypeSymbol, [NotNullWhen(true)] out Diagnostic? diagnostic)
        {
            diagnostic = null;
            if (methodBody.ContainsUsingStatement())
            {
                return false;
            }

            bool IsAwaitCandidateForOptimization(AwaitExpressionSyntax awaitExpression)
            {
                return awaitExpression.Parent is ReturnStatementSyntax returnStatement
                       && !HasCovariantReturn(semanticModel, awaitExpression, returnTypeSymbol)
                       && !returnStatement.HasParent(SyntaxKind.TryStatement)
                       && !returnStatement.HasParent(SyntaxKind.UsingStatement)
                       && !(returnStatement.Parent is BlockSyntax block && block.ContainsUsingStatement());
            }

            var awaitExpressions = methodBody.DescendantNodes(node => !node.IsKind(SyntaxKind.LocalFunctionStatement))
                .OfType<AwaitExpressionSyntax>()
                .ToList();
            var returnStatements = methodBody.DescendantNodes(node => !node.IsKind(SyntaxKind.LocalFunctionStatement)).OfType<ReturnStatementSyntax>();
            if (returnStatements.All(r => r.Expression.IsKind(SyntaxKind.AwaitExpression)) && awaitExpressions.All(IsAwaitCandidateForOptimization))
            {
                var additionalLocations = awaitExpressions.Skip(1).Select(a => a.GetLocation());
                diagnostic = Diagnostic.Create(s_rule, awaitExpressions[0].GetLocation(), additionalLocations);

                return true;
            }

            return false;
        }

        private static bool HasCovariantReturn(SemanticModel semanticModel, AwaitExpressionSyntax awaitExpression, ITypeSymbol returnTypeSymbol)
        {
            if (awaitExpression.Expression is InvocationExpressionSyntax invocation)
            {
                return HasCovariantReturn(semanticModel, invocation, returnTypeSymbol);
            }

            if (awaitExpression.Expression is ObjectCreationExpressionSyntax objectCreationExpression)
            {
                var typeSymbol = semanticModel.GetSymbolInfo(objectCreationExpression.Type).Symbol;

                return typeSymbol is not null && !returnTypeSymbol.Equals(typeSymbol, SymbolEqualityComparer.Default);
            }

            return false;
        }

        private static bool HasCovariantReturn(SemanticModel semanticModel, InvocationExpressionSyntax invocation, ITypeSymbol returnTypeSymbol)
        {
            var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol?.Name == "ConfigureAwait" && invocation.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax memberInvocation })
            {
                return HasCovariantReturn(semanticModel, memberInvocation, returnTypeSymbol);
            }

            var returnStatementTypeSymbol = methodSymbol?.ReturnType;

            return returnStatementTypeSymbol is not null && !returnTypeSymbol.Equals(returnStatementTypeSymbol, SymbolEqualityComparer.Default);
        }
    }
}