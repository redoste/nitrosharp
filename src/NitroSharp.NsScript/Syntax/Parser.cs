﻿using NitroSharp.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace NitroSharp.NsScript.Syntax
{
    internal sealed class Parser
    {
        private readonly Lexer _lexer;
        private readonly SyntaxToken[] _tokens;
        private int _tokenOffset;

        // It's not always possible for the lexer to tell whether something is a string literal
        // or an identifier, since some identifiers (more specifically, parameter names and
        // parameter references) in NSS can also be enclosed in quotes. In such cases, the lexer
        // outputs a StringLiteralOrQuotedIdentifier token and lets the parser decide whether
        // it's a string literal or an identifier. In order to do that, the parser needs to
        // keep track of the parameters that can be referenced in the current scope.
        //
        // Example:
        // function foo("stringParameter1", "stringParameter2") {
        //                     ↑                   ↑
        //     $bar = "stringParameter1" + "stringParameter2";
        // }             <identifier>         <identifier>
        private readonly Dictionary<string, Parameter> _parameterMap;

        private readonly StringInternTable _internTable;
        private readonly DiagnosticBuilder _diagnostics;

        private readonly ImmutableArray<Parameter>.Builder _parameters;
        private readonly ImmutableArray<DialogueBlock>.Builder _dialogueBlocks;

        public Parser(Lexer lexer)
        {
            _lexer = lexer;
            _diagnostics = new DiagnosticBuilder();
            _internTable = new StringInternTable();
            _tokens = Lex();
            _parameterMap = new Dictionary<string, Parameter>();
            _parameters = ImmutableArray.CreateBuilder<Parameter>();
            _dialogueBlocks = ImmutableArray.CreateBuilder<DialogueBlock>();
            if (_tokens.Length > 0)
            {
                CurrentToken = _tokens[0];
            }
        }

        private SyntaxToken PeekToken(int n) => _tokens[_tokenOffset + n];
        private SyntaxToken CurrentToken;

        private SourceText SourceText => _lexer.SourceText;

        internal DiagnosticBuilder DiagnosticBuilder => _diagnostics;

        private SyntaxToken[] Lex()
        {
            int capacity = Math.Max(32, SourceText.Source.Length / 6);
            var tokens = new ArrayBuilder<SyntaxToken>(capacity);
            ref SyntaxToken token = ref tokens.Add();
            do
            {
                _lexer.Lex(ref token);
                if (token.Kind == SyntaxTokenKind.EndOfFileToken)
                {
                    break;
                }

                token = ref tokens.Add();

            } while (token.Kind != SyntaxTokenKind.EndOfFileToken);

            return tokens.UnderlyingArray;
        }

        private SyntaxToken EatToken()
        {
            SyntaxToken ct = CurrentToken;
            CurrentToken = _tokens[++_tokenOffset];
            return ct;
        }

        private SyntaxToken EatToken(SyntaxTokenKind expectedKind)
        {
            SyntaxToken ct = CurrentToken;
            if (ct.Kind != expectedKind)
            {
                return CreateMissingToken(expectedKind, ct.Kind);
            }

            CurrentToken = _tokens[++_tokenOffset];
            return ct;
        }

        private string GetText(in SyntaxToken token)
            => SourceText.GetText(token.TextSpan);

        private string GetValueText(in SyntaxToken token)
            => SourceText.GetText(token.GetValueSpan());

        private string InternValueText(in SyntaxToken token)
            => _internTable.Add(SourceText.GetCharacterSpan(token.GetValueSpan()));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private SyntaxToken CreateMissingToken(SyntaxTokenKind expected, SyntaxTokenKind actual)
        {
            TokenExpected(expected, actual);
            TextSpan span = GetSpanForMissingToken();
            return new SyntaxToken(SyntaxTokenKind.MissingToken, span, SyntaxTokenFlags.Empty);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EatTokens(int count)
        {
            _tokenOffset += count;
            CurrentToken = _tokens[_tokenOffset];
        }

        private TextSpan SpanFrom(SyntaxNode firstNode)
            => TextSpan.FromBounds(firstNode.Span.Start, CurrentToken.TextSpan.Start);

        private TextSpan SpanFrom(in SyntaxToken firstToken)
            => TextSpan.FromBounds(firstToken.TextSpan.Start, CurrentToken.TextSpan.Start);

        private void EatStrayToken()
        {
            SyntaxToken token = PeekToken(0);
            Report(DiagnosticId.StrayToken, GetText(token));
            EatToken();
        }

        // Statement terminator characters used in NSS: ';', ':'.
        // There may be more than one terminator character in a row.
        private void EatStatementTerminator()
        {
            int tokensConsumed = 0;
            while (SyntaxFacts.IsStatementTerminator(CurrentToken.Kind))
            {
                EatToken();
                tokensConsumed++;
            }

            if (tokensConsumed == 0)
            {
                _diagnostics.Report(DiagnosticId.MissingStatementTerminator, GetSpanForMissingToken());
            }
        }

        public SourceFileRoot ParseSourceFile()
        {
            var fileReferences = ImmutableArray.CreateBuilder<Spanned<string>>();
            (uint chapterCount, uint sceneCount, uint functionCount) subroutineCounts = default;
            SyntaxTokenKind tk;
            while ((tk = CurrentToken.Kind) != SyntaxTokenKind.EndOfFileToken
                   && !SyntaxFacts.CanStartDeclaration(tk))
            {
                switch (CurrentToken.Kind)
                {
                    case SyntaxTokenKind.IncludeDirective:
                        EatToken();
                        SyntaxToken filePath = EatToken(SyntaxTokenKind.StringLiteralOrQuotedIdentifier);
                        fileReferences.Add(new Spanned<string>(GetValueText(filePath), filePath.TextSpan));
                        break;
                    case SyntaxTokenKind.Semicolon:
                        Report(DiagnosticId.MisplacedSemicolon);
                        EatToken();
                        break;
                    default:
                        EatStrayToken();
                        break;
                }
            }

            var subrotuines = ImmutableArray.CreateBuilder<SubroutineDeclaration>();
            while (CurrentToken.Kind != SyntaxTokenKind.EndOfFileToken)
            {
                _dialogueBlocks.Clear();
                switch (CurrentToken.Kind)
                {
                    case SyntaxTokenKind.ChapterKeyword:
                        subrotuines.Add(ParseChapterDeclaration());
                        subroutineCounts.chapterCount++;
                        break;
                    case SyntaxTokenKind.SceneKeyword:
                        subrotuines.Add(ParseSceneDeclaration());
                        subroutineCounts.sceneCount++;
                        break;
                    case SyntaxTokenKind.FunctionKeyword:
                        subrotuines.Add(ParseFunctionDeclaration());
                        subroutineCounts.functionCount++;
                        break;
                    // Lines starting with a '.' are treated as comments.
                    case SyntaxTokenKind.Dot:
                        SkipToNextLine();
                        break;
                    case SyntaxTokenKind.EndOfFileToken:
                        break;
                    default:
                        Report(DiagnosticId.ExpectedSubroutineDeclaration, GetText(CurrentToken));
                        SkipToNextLine();
                        break;
                }
            }

            var span = new TextSpan(0, SourceText.Length);
            return new SourceFileRoot(
                subrotuines.ToImmutable(),
                fileReferences.ToImmutable(),
                subroutineCounts,
                span
            );
        }

        public SubroutineDeclaration ParseSubroutineDeclaration()
        {
            _dialogueBlocks.Clear();
            switch (CurrentToken.Kind)
            {
                case SyntaxTokenKind.ChapterKeyword:
                    return ParseChapterDeclaration();
                case SyntaxTokenKind.SceneKeyword:
                    return ParseSceneDeclaration();
                case SyntaxTokenKind.FunctionKeyword:
                    _parameterMap.Clear();
                    return ParseFunctionDeclaration();
                default:
                    throw new InvalidOperationException($"{CurrentToken.Kind} cannot start a declaration.");
            }
        }

        private ChapterDeclaration ParseChapterDeclaration()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.ChapterKeyword);
            Spanned<string> name = ParseIdentifier();
            Block body = ParseBlock();
            ImmutableArray<DialogueBlock> dialogueBlocks = _dialogueBlocks.ToImmutable();
            return new ChapterDeclaration(name, body, dialogueBlocks, SpanFrom(keyword));
        }

        private SceneDeclaration ParseSceneDeclaration()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.SceneKeyword);
            Spanned<string> name = ParseIdentifier();
            Block body = ParseBlock();
            ImmutableArray<DialogueBlock> dialogueBlocks = _dialogueBlocks.ToImmutable();
            return new SceneDeclaration(name, body, dialogueBlocks, SpanFrom(keyword));
        }

        private FunctionDeclaration ParseFunctionDeclaration()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.FunctionKeyword);
            Spanned<string> name = ParseIdentifier();
            ImmutableArray<Parameter> parameters = ParseParameterList();

            Block body = ParseBlock();
            ImmutableArray<DialogueBlock> dialogueBlocks = _dialogueBlocks.ToImmutable();
            return new FunctionDeclaration(name, parameters, body, dialogueBlocks, SpanFrom(keyword));
        }

        private ImmutableArray<Parameter> ParseParameterList()
        {
            EatToken(SyntaxTokenKind.OpenParen);

            _parameters.Clear();
            while (CurrentToken.Kind != SyntaxTokenKind.CloseParen
                && CurrentToken.Kind != SyntaxTokenKind.EndOfFileToken)
            {
                switch (CurrentToken.Kind)
                {
                    case SyntaxTokenKind.Identifier:
                    case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                        Spanned<string> identifier = ParseIdentifier();
                        var parameter = new Parameter(identifier.Value, identifier.Span);
                        _parameters.Add(parameter);
                        _parameterMap[parameter.Name] = parameter;
                        break;
                    case SyntaxTokenKind.Comma:
                        EatToken();
                        break;
                    default:
                        EatStrayToken();
                        break;
                }
            }

            EatToken(SyntaxTokenKind.CloseParen);
            return _parameters.ToImmutable();
        }

        private Block ParseBlock()
        {
            SyntaxToken openBrace = EatToken(SyntaxTokenKind.OpenBrace);
            ImmutableArray<Statement> statements = ParseStatements();
            EatToken(SyntaxTokenKind.CloseBrace);
            return new Block(statements, SpanFrom(openBrace));
        }

        private ImmutableArray<Statement> ParseStatements()
        {
            var statements = ImmutableArray.CreateBuilder<Statement>();
            SyntaxTokenKind tk;
            while ((tk = CurrentToken.Kind) != SyntaxTokenKind.CloseBrace
                   && tk != SyntaxTokenKind.EndOfFileToken)
            {
                Statement? statement = ParseStatement();
                if (statement != null)
                {
                    statements.Add(statement);
                    if (statement.Kind == SyntaxNodeKind.DialogueBlock)
                    {
                        _dialogueBlocks.Add((DialogueBlock)statement);
                    }
                }
            }

            return statements.ToImmutable();
        }

        internal Statement? ParseStatement()
        {
            Statement? statement;
            do
            {
                statement = ParseStatementCore();
                if (statement != null) { break; }
                SyntaxTokenKind tk = CurrentToken.Kind;
                if (tk == SyntaxTokenKind.EndOfFileToken || tk == SyntaxTokenKind.CloseBrace)
                {
                    return null;
                }
            } while (true);

            return statement;
        }

        private Statement? ParseStatementCore()
        {
            switch (CurrentToken.Kind)
            {
                case SyntaxTokenKind.OpenBrace:
                    return ParseBlock();
                case SyntaxTokenKind.IfKeyword:
                    return ParseIfStatement();
                case SyntaxTokenKind.BreakKeyword:
                    return ParseBreakStatement();
                case SyntaxTokenKind.WhileKeyword:
                    return ParseWhileStatement();
                case SyntaxTokenKind.ReturnKeyword:
                    return ParseReturnStatement();
                case SyntaxTokenKind.SelectKeyword:
                    return ParseSelectStatement();
                case SyntaxTokenKind.CaseKeyword:
                    return ParseSelectSection();
                case SyntaxTokenKind.CallChapterKeyword:
                    return ParseCallChapterStatement();
                case SyntaxTokenKind.CallSceneKeyword:
                    return ParseCallSceneStatement();
                case SyntaxTokenKind.DialogueBlockStartTag:
                    return ParseDialogueBlock();
                case SyntaxTokenKind.Markup:
                    SyntaxToken token = EatToken();
                    return new MarkupNode(GetText(token), token.TextSpan);
                case SyntaxTokenKind.MarkupBlankLine:
                    token = EatToken();
                    return new MarkupBlankLine(token.TextSpan);
                case SyntaxTokenKind.LessThan:
                    if (SkipStrayMarkupNodeIfApplicable())
                    {
                        return null;
                    }
                    goto default;
                case SyntaxTokenKind.Dot:
                    SkipToNextLine();
                    return null;

                case SyntaxTokenKind.Identifier:
                case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                    if (IsArgumentListOrSemicolon())
                    {
                        return ParseFunctionCallWithOmittedParentheses();
                    }
                    goto default;

                default:
                    return ParseExpressionStatement();
            }
        }

        private bool SkipStrayMarkupNodeIfApplicable()
        {
            Debug.Assert(CurrentToken.Kind == SyntaxTokenKind.LessThan);
            int currentLine = GetLineNumber();

            int n = 0;
            SyntaxToken token;
            // Look for the closing '>'
            while ((token = PeekToken(n)).Kind != SyntaxTokenKind.GreaterThan)
            {
                if (token.Kind == SyntaxTokenKind.EndOfFileToken)
                {
                    return false;
                }

                n++;
            }

            // Check if the current line ends with the '>' character that we found
            if (GetLineNumber(PeekToken(n + 1)) != currentLine)
            {
                Report(DiagnosticId.StrayMarkupBlock, SourceText.Lines[currentLine]);
                EatTokens(n + 1); // skip to the next line
                return true;
            }

            return false;
        }

        private ExpressionStatement? ParseExpressionStatement()
        {
            Expression? expr = ParseExpression();
            if (expr == null) { return null; }

            if (!SyntaxFacts.IsStatementExpression(expr))
            {
                Report(DiagnosticId.InvalidExpressionStatement, expr.Span);
                EatStatementTerminator();
                return null;
            }

            EatStatementTerminator();
            return new ExpressionStatement(expr, SpanFrom(expr));
        }

        private ExpressionStatement? ParseFunctionCallWithOmittedParentheses()
        {
            FunctionCallExpression? call = ParseFunctionCall();
            if (call == null) { return null; }
            EatStatementTerminator();
            return new ExpressionStatement(call, SpanFrom(call));
        }

        internal Expression? ParseExpression()
        {
            return ParseSubExpression(Precedence.Expression);
        }

        private enum Precedence
        {
            Expression = 0,
            Assignment,
            Logical,
            Equality,
            Relational,
            Additive,
            Multiplicative,
            Unary
        }

        private static Precedence GetPrecedence(BinaryOperatorKind operatorKind)
        {
            switch (operatorKind)
            {
                case BinaryOperatorKind.Multiply:
                case BinaryOperatorKind.Divide:
                case BinaryOperatorKind.Remainder:
                    return Precedence.Multiplicative;

                case BinaryOperatorKind.Add:
                case BinaryOperatorKind.Subtract:
                    return Precedence.Additive;

                case BinaryOperatorKind.GreaterThan:
                case BinaryOperatorKind.GreaterThanOrEqual:
                case BinaryOperatorKind.LessThan:
                case BinaryOperatorKind.LessThanOrEqual:
                    return Precedence.Relational;

                case BinaryOperatorKind.Equals:
                case BinaryOperatorKind.NotEquals:
                    return Precedence.Equality;

                case BinaryOperatorKind.And:
                case BinaryOperatorKind.Or:
                    return Precedence.Logical;

                default:
                    throw ThrowHelper.IllegalValue(nameof(operatorKind));
            }
        }

        private Expression? ParseSubExpression(Precedence minPrecedence)
        {
            Expression? leftOperand;
            Precedence newPrecedence;

            SyntaxTokenKind tk = CurrentToken.Kind;
            TextSpan tkSpan = CurrentToken.TextSpan;
            if (SyntaxFacts.TryGetUnaryOperatorKind(tk, out UnaryOperatorKind unaryOperator))
            {
                EatToken();
                newPrecedence = Precedence.Unary;
                Expression? operand = ParseSubExpression(newPrecedence);
                if (operand == null) { return null; }
                var fullSpan = TextSpan.FromBounds(tkSpan.Start, operand.Span.End);
                leftOperand = new UnaryExpression(
                    operand, new Spanned<UnaryOperatorKind>(unaryOperator, tkSpan), fullSpan);
            }
            else
            {
                leftOperand = ParseTerm(minPrecedence);
                if (leftOperand == null)
                {
                    return null;
                }
            }

            while (true)
            {
                tk = CurrentToken.Kind;
                tkSpan = CurrentToken.TextSpan;
                bool binary;
                AssignmentOperatorKind assignOpKind = default;
                if (SyntaxFacts.TryGetBinaryOperatorKind(tk, out BinaryOperatorKind binOpKind))
                {
                    binary = true;
                }
                else if (SyntaxFacts.TryGetAssignmentOperatorKind(tk, out assignOpKind))
                {
                    binary = false;
                }
                else
                {
                    break;
                }

                newPrecedence = binary ? GetPrecedence(binOpKind) : Precedence.Assignment;
                if (newPrecedence < minPrecedence)
                {
                    break;
                }

                EatToken();

                bool hasRightOperand = assignOpKind != AssignmentOperatorKind.Increment
                                       && assignOpKind != AssignmentOperatorKind.Decrement;
                Expression? rightOperand = hasRightOperand
                    ? ParseSubExpression(newPrecedence)
                    : leftOperand;

                if (rightOperand == null)
                {
                    return null;
                }

                var span = TextSpan.FromBounds(tkSpan.Start, rightOperand.Span.End);
                leftOperand = binary
                    ? (Expression)new BinaryExpression(
                        leftOperand, new Spanned<BinaryOperatorKind>(binOpKind, tkSpan), rightOperand, span)
                    : new AssignmentExpression(
                        leftOperand, new Spanned<AssignmentOperatorKind>(assignOpKind, tkSpan), rightOperand, span);
            }

            return leftOperand;
        }

        private Expression? ParseTerm(Precedence precedence)
        {
            switch (CurrentToken.Kind)
            {
                case SyntaxTokenKind.Identifier:
                    return IsFunctionCall()
                        ? (Expression?)ParseFunctionCall()
                        : ParseNameExpression();

                case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                    return IsParameter() || (CurrentToken.Flags & SyntaxTokenFlags.HasDollarPrefix) == SyntaxTokenFlags.HasDollarPrefix
                        ? (Expression)ParseNameExpression()
                        : ParseLiteral();

                case SyntaxTokenKind.NumericLiteral:
                case SyntaxTokenKind.NullKeyword:
                case SyntaxTokenKind.TrueKeyword:
                case SyntaxTokenKind.FalseKeyword:
                    return ParseLiteral();

                case SyntaxTokenKind.OpenParen:
                    SyntaxToken openParen = EatToken(SyntaxTokenKind.OpenParen);
                    Expression? expr = ParseSubExpression(Precedence.Expression);
                    if (expr == null) { return null; }
                    if (CurrentToken.Kind == SyntaxTokenKind.Comma)
                    {
                        return ParseBezierExpression(openParen, expr);
                    }
                    EatToken(SyntaxTokenKind.CloseParen);
                    return expr;

                default:
                    Report(DiagnosticId.InvalidExpressionTerm, GetText(CurrentToken));
                    EatToken();
                    return null;
            }
        }

        private BezierExpression? ParseBezierExpression(in SyntaxToken openParen, Expression x0)
        {
            BezierControlPoint? parseControlPoint(bool starting)
            {
                (SyntaxTokenKind startTk, SyntaxTokenKind endTk) = starting
                    ? (SyntaxTokenKind.OpenParen, SyntaxTokenKind.CloseParen)
                    : (SyntaxTokenKind.OpenBrace, SyntaxTokenKind.CloseBrace);
                EatToken(startTk);
                Expression? x = ParseSubExpression(Precedence.Expression);
                if (x == null) { return null; }
                EatToken(SyntaxTokenKind.Comma);
                Expression? y = ParseSubExpression(Precedence.Expression);
                if (y == null) { return null; }
                EatToken(endTk);
                return new BezierControlPoint(x, y, starting);
            }

            var controlPoints = ImmutableArray.CreateBuilder<BezierControlPoint>();
            EatToken(SyntaxTokenKind.Comma);
            Expression? y0 = ParseSubExpression(Precedence.Expression);
            if (y0 != null)
            {
                controlPoints.Add(new BezierControlPoint(x0, y0, starting: true));
            }
            EatToken(SyntaxTokenKind.CloseParen);
            while (CurrentToken.Kind != SyntaxTokenKind.EndOfFileToken)
            {
                bool? paren = CurrentToken.Kind switch
                {
                    SyntaxTokenKind.OpenParen => true,
                    SyntaxTokenKind.OpenBrace => false,
                    _ => null
                };
                if (paren == null) { break; }
                BezierControlPoint? cp = parseControlPoint(paren.Value);
                if (cp == null) { return null; }
                controlPoints.Add(cp.Value);
            }

            return new BezierExpression(controlPoints.ToImmutable(), SpanFrom(openParen));
        }

        private LiteralExpression ParseLiteral()
        {
            SyntaxToken token = EatToken();
            ConstantValue value;
            switch (token.Kind)
            {
                case SyntaxTokenKind.NumericLiteral:
                    ReadOnlySpan<char> valueText = SourceText.GetCharacterSpan(token.GetValueSpan());
                    var numberStyle = token.IsHexTriplet ? NumberStyles.HexNumber : NumberStyles.None;
                    value = token.IsFloatingPointLiteral
                        ? ConstantValue.Number(float.Parse(valueText, provider: CultureInfo.InvariantCulture))
                        : ConstantValue.Number(int.Parse(valueText, numberStyle));
                    break;
                case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                    string str = InternValueText(token);
                    value = ConstantValue.String(str);
                    break;
                case SyntaxTokenKind.NullKeyword:
                    value = ConstantValue.Null;
                    break;
                case SyntaxTokenKind.TrueKeyword:
                    value = ConstantValue.True;
                    break;
                case SyntaxTokenKind.FalseKeyword:
                    value = ConstantValue.False;
                    break;
                default:
                    ThrowHelper.Unreachable();
                    return null!;
            }

            return new LiteralExpression(value, token.TextSpan);
        }

        private Spanned<string> ParseIdentifier()
        {
            SyntaxToken token = EatToken();
            switch (token.Kind)
            {
                case SyntaxTokenKind.Identifier:
                case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                default:
                    return new Spanned<string>(InternValueText(token), token.TextSpan);
            }
        }

        private NameExpression ParseNameExpression()
        {
            Debug.Assert(CurrentToken.Kind == SyntaxTokenKind.Identifier
                      || CurrentToken.Kind == SyntaxTokenKind.StringLiteralOrQuotedIdentifier);

            SyntaxToken token = EatToken();
            var identifier = new Spanned<string>(InternValueText(token), token.TextSpan);
            return new NameExpression(identifier.Value, token.GetSigil(), identifier.Span);
        }

        private bool IsFunctionCall()
        {
            return PeekToken(1).Kind == SyntaxTokenKind.OpenParen;
        }

        private bool IsArgumentListOrSemicolon()
        {
            SyntaxTokenKind peek;
            int n = 0;
            while ((peek = PeekToken(n).Kind) != SyntaxTokenKind.EndOfFileToken)
            {
                switch (peek)
                {
                    case SyntaxTokenKind.NullKeyword:
                    case SyntaxTokenKind.TrueKeyword:
                    case SyntaxTokenKind.FalseKeyword:
                    case SyntaxTokenKind.Identifier:
                    case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                    case SyntaxTokenKind.NumericLiteral:
                    case SyntaxTokenKind.Comma:
                    case SyntaxTokenKind.Dot:
                        n++;
                        break;

                    case SyntaxTokenKind.Semicolon:
                    case SyntaxTokenKind.CloseBrace:
                        return true;

                    default:
                        return false;
                }
            }

            return false;
        }

        private bool IsParameter()
        {
            switch (CurrentToken.Kind)
            {
                case SyntaxTokenKind.Identifier:
                case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                    return _parameterMap.ContainsKey(InternValueText(CurrentToken));
                default:
                    return false;
            }
        }

        private FunctionCallExpression? ParseFunctionCall()
        {
            Spanned<string> targetName = ParseIdentifier();
            ImmutableArray<Expression>? args = ParseArgumentList();
            if (!args.HasValue) { return null; }
            var span = TextSpan.FromBounds(targetName.Span.Start, CurrentToken.TextSpan.Start);
            return new FunctionCallExpression(targetName, args.Value, span);
        }

        private ImmutableArray<Expression>? ParseArgumentList()
        {
            if (SyntaxFacts.IsStatementTerminator(CurrentToken.Kind))
            {
                return ImmutableArray<Expression>.Empty;
            }

            EatToken(SyntaxTokenKind.OpenParen);
            SyntaxTokenKind tk = CurrentToken.Kind;
            if (tk == SyntaxTokenKind.CloseParen)
            {
                EatToken();
                return ImmutableArray<Expression>.Empty;
            }

            var arguments = ImmutableArray.CreateBuilder<Expression>();
            while ((tk = CurrentToken.Kind) != SyntaxTokenKind.CloseParen
                   && tk != SyntaxTokenKind.Semicolon
                   && tk != SyntaxTokenKind.EndOfFileToken)
            {
                switch (tk)
                {
                    case SyntaxTokenKind.NumericLiteral:
                    case SyntaxTokenKind.StringLiteralOrQuotedIdentifier:
                    case SyntaxTokenKind.Identifier:
                    case SyntaxTokenKind.NullKeyword:
                    case SyntaxTokenKind.TrueKeyword:
                    case SyntaxTokenKind.FalseKeyword:
                        Expression? arg = ParseExpression();
                        if (arg == null) { return null; }
                        arguments.Add(arg);
                        break;

                    case SyntaxTokenKind.Comma:
                    case SyntaxTokenKind.Dot:
                    // Ampersand? Why?
                    case SyntaxTokenKind.Ampersand:
                        EatToken();
                        break;

                    default:
                        Expression? expr = ParseExpression();
                        if (expr == null) { return null; }
                        arguments.Add(expr);
                        break;
                }
            }

            EatToken(SyntaxTokenKind.CloseParen);
            return arguments.ToImmutable();
        }

        private IfStatement? ParseIfStatement()
        {
            SyntaxToken ifKeyword = EatToken(SyntaxTokenKind.IfKeyword);
            EatToken(SyntaxTokenKind.OpenParen);
            Expression? condition = ParseExpression();
            if (condition == null) { return null; }
            EatToken(SyntaxTokenKind.CloseParen);

            Statement? ifTrue = ParseStatement();
            if (ifTrue == null) { return null; }
            Statement? ifFalse = null;
            if (CurrentToken.Kind == SyntaxTokenKind.ElseKeyword)
            {
                EatToken();
                ifFalse = ParseStatement();
            }

            return new IfStatement(condition, ifTrue, ifFalse, SpanFrom(ifKeyword));
        }

        private BreakStatement ParseBreakStatement()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.BreakKeyword);
            EatStatementTerminator();
            return new BreakStatement(SpanFrom(keyword));
        }

        private WhileStatement? ParseWhileStatement()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.WhileKeyword);
            EatToken(SyntaxTokenKind.OpenParen);
            Expression? condition = ParseExpression();
            if (condition == null) { return null; }
            EatToken(SyntaxTokenKind.CloseParen);
            Statement? body = ParseStatement();
            if (body == null) { return null; }
            return new WhileStatement(condition, body, SpanFrom(keyword));
        }

        private ReturnStatement ParseReturnStatement()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.ReturnKeyword);
            EatStatementTerminator();
            return new ReturnStatement(SpanFrom(keyword));
        }

        private SelectStatement ParseSelectStatement()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.SelectKeyword);
            Block body = ParseBlock();
            return new SelectStatement(body, SpanFrom(keyword));
        }

        private SelectSection ParseSelectSection()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.CaseKeyword);
            Spanned<string> labelName = ConsumeTextUntil(
                tk => tk == SyntaxTokenKind.OpenBrace || tk == SyntaxTokenKind.Colon
            );
            if (CurrentToken.Kind == SyntaxTokenKind.Colon)
            {
                EatToken();
            }

            Block body = ParseBlock();
            return new SelectSection(labelName, body, SpanFrom(keyword));
        }

        private CallChapterStatement ParseCallChapterStatement()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.CallChapterKeyword);
            Spanned<string> filePath = ConsumeTextUntil(tk => tk == SyntaxTokenKind.Semicolon);
            EatStatementTerminator();
            return new CallChapterStatement(filePath, SpanFrom(keyword));
        }

        private CallSceneStatement ParseCallSceneStatement()
        {
            SyntaxToken keyword = EatToken(SyntaxTokenKind.CallSceneKeyword);
            (Spanned<string>? file, Spanned<string> scene) = ParseSymbolPath();
            EatStatementTerminator();
            return new CallSceneStatement(file, scene, SpanFrom(keyword));
        }

        // Parses call_scene specific symbol path syntax.
        // call_scene can be followed by either '@->{localSymbolName}' (e.g. '@->SelectStoryModeA')
        // or '{filepath}->{symbolName}' (e.g. 'nss/extra_gallery.nss->extra_gallery_main').
        private (Spanned<string>? filePath, Spanned<string> symbolName) ParseSymbolPath()
        {
            if (CurrentToken.Kind == SyntaxTokenKind.AtArrow)
            {
                EatToken();
            }

            Spanned<string>? filePath = null;
            Spanned<string> symbolName = default;
            Spanned<string> part = ConsumeTextUntil(
                tk => tk == SyntaxTokenKind.Semicolon || tk == SyntaxTokenKind.Arrow
            );
            if (CurrentToken.Kind == SyntaxTokenKind.Arrow)
            {
                EatToken();
                filePath = part;
                symbolName = ConsumeTextUntil(tk => tk == SyntaxTokenKind.Semicolon);
            }
            else
            {
                symbolName = part;
            }

            return (filePath, symbolName);
        }

        // Consumes tokens until the specified condition is met.
        private Spanned<string> ConsumeTextUntil(Func<SyntaxTokenKind, bool> condition)
        {
            SyntaxTokenKind tk;
            int start = CurrentToken.TextSpan.Start;
            int end = 0;
            while ((tk = CurrentToken.Kind) != SyntaxTokenKind.EndOfFileToken && !condition(tk))
            {
                end = EatToken().TextSpan.End;
            }

            var span = TextSpan.FromBounds(start, end);
            return new Spanned<string>(SourceText.GetText(span), span);
        }

        private DialogueBlock ParseDialogueBlock()
        {
            string extractBoxName(in SyntaxToken tag)
            {
                ReadOnlySpan<char> span = SourceText.GetCharacterSpan(tag.TextSpan);
                span = span[5..^1];
                Debug.Assert(span.Length > 0);
                return span.ToString();
            }

            string extractBlockName(in SyntaxToken identifierToken)
            {
                ReadOnlySpan<char> span = SourceText.GetCharacterSpan(identifierToken.TextSpan);
                return span.Length > 2 ? span[1..^1].ToString() : "";
            }

            SyntaxToken startTag = EatToken(SyntaxTokenKind.DialogueBlockStartTag);
            string associatedBox = extractBoxName(startTag);
            SyntaxToken blockIdentifier = EatToken(SyntaxTokenKind.DialogueBlockIdentifier);
            string name = extractBlockName(blockIdentifier);

            var statements = ImmutableArray.CreateBuilder<Statement>();
            while (CurrentToken.Kind is not
                (SyntaxTokenKind.DialogueBlockEndTag or SyntaxTokenKind.EndOfFileToken))
            {
                Statement? statement = ParseStatement();
                if (statement != null)
                {
                    statements.Add(statement);
                }
            }

            EatToken(SyntaxTokenKind.DialogueBlockEndTag);
            return new DialogueBlock(
                name, associatedBox, statements.ToImmutable(), SpanFrom(startTag));
        }

        private int GetLineNumber()
            => SourceText.GetLineNumberFromPosition(CurrentToken.TextSpan.Start);

        private int GetLineNumber(SyntaxToken token)
            => SourceText.GetLineNumberFromPosition(token.TextSpan.Start);

        private void SkipToNextLine()
        {
            int currentLine = GetLineNumber();
            int lineCount = SourceText.Lines.Count;
            do
            {
                SyntaxToken tk = EatToken();
                if (tk.Kind == SyntaxTokenKind.EndOfFileToken)
                {
                    break;
                }

            } while (currentLine <= lineCount && GetLineNumber() == currentLine);
        }

        private void Report(DiagnosticId diagnosticId)
        {
            _diagnostics.Report(diagnosticId, CurrentToken.TextSpan);
        }

        private void Report(DiagnosticId diagnosticId, TextSpan span)
        {
            _diagnostics.Report(diagnosticId, span);
        }

        private void Report(DiagnosticId diagnosticId, params object[] arguments)
        {
            _diagnostics.Report(diagnosticId, CurrentToken.TextSpan, arguments);
        }

        private TextSpan GetSpanForMissingToken()
        {
            if (_tokenOffset > 0)
            {
                SyntaxToken prevToken = PeekToken(-1);
                TextSpan prevTokenLineSpan = SourceText.GetLineSpanFromPosition(prevToken.TextSpan.End);
                TextSpan currentLineSpan = SourceText.GetLineSpanFromPosition(CurrentToken.TextSpan.Start);
                if (currentLineSpan != prevTokenLineSpan)
                {
                    int newLineSequenceLength = currentLineSpan.Start - prevTokenLineSpan.End;
                    return new TextSpan(prevTokenLineSpan.End, newLineSequenceLength);
                }
            }

            return CurrentToken.TextSpan;
        }

        private void TokenExpected(SyntaxTokenKind expected, SyntaxTokenKind actual)
        {
            string expectedText = SyntaxFacts.GetText(expected);
            string actualText = SyntaxFacts.GetText(actual);

            Report(DiagnosticId.TokenExpected, expectedText, actualText);
        }
    }
}
