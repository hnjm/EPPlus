﻿using OfficeOpenXml.Core.CellStore;
using OfficeOpenXml.FormulaParsing.Excel.Functions;
using OfficeOpenXml.FormulaParsing.ExcelUtilities;
using OfficeOpenXml.FormulaParsing.Exceptions;
using OfficeOpenXml.FormulaParsing.ExpressionGraph;
using OfficeOpenXml.FormulaParsing.ExpressionGraph.FunctionCompilers;
using OfficeOpenXml.FormulaParsing.ExpressionGraph.Rpn;
using OfficeOpenXml.FormulaParsing.LexicalAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OfficeOpenXml.FormulaParsing
{
    internal class RpnOptimizedDependencyChain
    {
        internal List<RpnFormula> formulas = new List<RpnFormula>();
        internal Stack<RpnFormula> _formulaStack=new Stack<RpnFormula>();
        internal Dictionary<int, RangeDictionary> accessedRanges = new Dictionary<int, RangeDictionary>();
        internal HashSet<ulong> processedCells = new HashSet<ulong>();
        internal List<ulong> _circularReferences = new List<ulong>();
        internal ISourceCodeTokenizer _tokenizer;
        internal RpnExpressionGraph _graph;
        internal ParsingContext _parsingContext;
        public RpnOptimizedDependencyChain(ExcelWorkbook wb)
        {
            _tokenizer = OptimizedSourceCodeTokenizer.Default;
            _parsingContext = ParsingContext.Create(wb._package);
            var dataProvider = new EpplusExcelDataProvider(wb._package, _parsingContext);
            _parsingContext.ExcelDataProvider = dataProvider;
            _parsingContext.NameValueProvider = new EpplusNameValueProvider(dataProvider);
            _parsingContext.RangeAddressFactory = new RangeAddressFactory(dataProvider, _parsingContext);
            _graph = new RpnExpressionGraph(_parsingContext);
        }

        internal void Add(RpnFormula f)
        {
            formulas.Add(f);
        }
        internal RpnOptimizedDependencyChain Execute()
        {
            return RpnFormulaExecution.Create(_parsingContext.Package.Workbook, new ExcelCalculationOption());
        }
    }
    internal class RpnFormulaExecution
    {
        internal static RpnOptimizedDependencyChain Create(ExcelWorkbook wb, ExcelCalculationOption options)
        {
            var depChain = new RpnOptimizedDependencyChain(wb);
            foreach (var ws in wb.Worksheets)
            {
                if (ws.IsChartSheet==false)
                {
                    ExecuteChain(depChain, wb.FormulaParser.Lexer, ws.Cells, options);
                }
            }
            foreach (var name in wb.Names)
            {
                if (name.NameValue == null)
                {
                    //GetChain(depChain, wb.FormulaParser.Lexer, name, options);
                }
            }
            return depChain;
        }
        internal static RpnOptimizedDependencyChain Create(ExcelWorksheet ws, ExcelCalculationOption options)
        {
            var depChain = new RpnOptimizedDependencyChain(ws.Workbook);

            ExecuteChain(depChain, ws.Workbook.FormulaParser.Lexer, ws.Cells, options);

            return depChain;
        }
        internal static RpnOptimizedDependencyChain Create(ExcelRange cells, ExcelCalculationOption options)
        {
            var depChain = new RpnOptimizedDependencyChain(cells._workbook);

            ExecuteChain(depChain, cells.Worksheet.Workbook.FormulaParser.Lexer, cells, options);

            return depChain;
        }

        private static void ExecuteChain(RpnOptimizedDependencyChain depChain, ILexer lexer, ExcelRange range, ExcelCalculationOption options)
        {
            var ws = range.Worksheet;
            RpnFormula f = null;
            var fs = new CellStoreEnumerator<object>(ws._formulas, range._fromRow, range._fromCol, range._toRow, range._toCol);
            while (fs.Next())
            {

                if (fs.Value == null || fs.Value.ToString().Trim() == "") continue;
                var id = ExcelCellBase.GetCellId(ws.IndexInList, fs.Row, fs.Column);
                if (depChain.processedCells.Contains(id) == false)
                {
                    depChain.processedCells.Add(id);
                    ws.Workbook.FormulaParser.ParsingContext.CurrentCell = new FormulaCellAddress(ws.IndexInList, fs.Row, fs.Column);
                    f=GetFormula(depChain, ws, fs);
                    AddChainForFormula(depChain, lexer, f, options);
                }
            }
        }

        private static RpnFormula GetFormula(RpnOptimizedDependencyChain depChain,  ExcelWorksheet ws, CellStoreEnumerator<object> fs)
        {
            if (fs.Value is int ix)
            {
                var sf = ws._sharedFormulas[ix];
                return ws._sharedFormulas[ix].GetRpnFormula(depChain, fs.Row, fs.Column);
            }
            else
            {
                var s = fs.Value.ToString();
                //compiler
                if (string.IsNullOrEmpty(s)) return null;
                var f = new RpnFormula(ws, fs.Row, fs.Column);
                f.SetFormula(s, depChain._tokenizer, depChain._graph);
                return f;
            }
        }

        internal class CalcState
        {
            internal Stack<Formula> _stack = new Stack<Formula>();

        }
        private static void AddChainForFormula(RpnOptimizedDependencyChain depChain, ILexer lexer, RpnFormula f, ExcelCalculationOption options)
        {
            FormulaRangeAddress address=null;
            var subCalcs = new Stack<CalcState>();
            var calcState = new CalcState();
            var ws = f._ws;
ExecuteFormula:
            depChain._graph.ExecuteNextExpression(f, ref address);
            var et = f._expressions;
            while (f._expressionIndex < et.Count)
            {
                var e = et[f._expressionIndex++];
                switch(e.ExpressionType)
                {
                    case ExpressionType.CellAddress:
                        address = e.GetAddress();
                        var isAdded = GetProcessedAddress(depChain, ref address);
                        if (isAdded==false || address == null)
                        {
                            goto ExecuteFormula;
                        }
                        else
                        {
                            goto FollowChain;
                        }
                    case ExpressionType.ExcelRange:
                    case ExpressionType.TableAddress:
                        break;
                    case ExpressionType.Function:
                        break;
                }
            }
            depChain.formulas.Add(f);
            if (depChain._formulaStack.Count > 0)
            {
                f = depChain._formulaStack.Pop();                
                goto NextFormula;
            }
            return;
        FollowChain:
            ws = depChain._parsingContext.Package.Workbook.Worksheets[address.WorksheetIx];
            f._formulaEnumerator = new CellStoreEnumerator<object>(ws._formulas, address.FromRow, address.FromCol, address.ToRow, address.ToCol);
        NextFormula:
            if (f._formulaEnumerator.Next())
            {
                depChain._formulaStack.Push(f);
                f=GetFormula(depChain, ws, f._formulaEnumerator);
                goto ExecuteFormula;
            }
            
            goto ExecuteFormula;
            //                var ae = et.AddressExpressions[f.AddressExpressionIndex++];
            //                if (ae.ExpressionType == ExpressionType.Function) goto FollowFormulaChain;
            //                if(ae._parent?.ExpressionType==ExpressionType.Function)
            //                {
            //                    var fe = ((FunctionExpression)ae._parent);
            //                    currentFunction = fe.Function;
            //                    switch(currentFunction.GetParameterInfo(fe.GetArgumentIndex(ae)))
            //                    {
            //                        case FunctionParameterInformation.IgnoreAddress:
            //                            goto FollowFormulaChain;
            //                        case FunctionParameterInformation.Condition:
            //                            subCalcs.Push(calcState);
            //                            calcState = new CalcState();

            //                            goto FollowFormulaChain;
            //                        default:
            //                            break;
            //                    }
            //                    if(currentFunction.ReturnsReference)
            //                    {
            //                        //fa.Stack
            //                        int i=1;d
            //                if (address.FromRow == address.ToRow && address.FromCol == address.ToCol)
            //                {
            //                    if (GetProcessedAddress(depChain, (int)address.WorksheetIx, address.FromRow, address.FromCol))                         
            //                    {
            //                        ExcelWorksheet fws;
            //                        if (address.WorksheetIx > 0)
            //                            fws = ws.Workbook.Worksheets[address.WorksheetIx];
            //                        else
            //                            fws = ws;

            //                        if(fws._formulas.Exists(address.FromRow, address.FromCol))
            //                        {
            //                            calcState._stack.Push(f);
            //                            var fv = fws._formulas.GetValue(address.FromRow, address.FromCol);
            //                            if (fv is int ix)
            //                            {
            //                                f = fws._sharedFormulas[ix].GetFormula(address.FromRow, address.FromCol);
            //                            }
            //                            else
            //                            {
            //                                var s = fv.ToString();
            //                                //compiler
            //                                if (string.IsNullOrEmpty(s)) goto FollowFormulaChain;
            //                                f = new Formula(fws, address.FromRow, address.FromCol, s);
            //                            }
            //                            depChain.processedCells.Add(f.Id);
            //                            ws = fws;
            //                            goto FollowFormulaChain;
            //                        }
            //                    }
            //                }
            //                else if (GetProcessedAddress(depChain, ref address))
            //                {
            //                    ExcelWorksheet fws;
            //                    if (address.WorksheetIx > 0)
            //                        fws = ws.Workbook.Worksheets[address.WorksheetIx];
            //                    else
            //                        fws = ws;

            //                    f._formulaEnumerator = new CellStoreEnumerator<object>(fws._formulas, address.FromRow, address.FromCol, address.ToRow, address.ToCol);
            //                    goto NextFormula;
            //                }
            //                if (f.AddressExpressionIndex < et.AddressExpressions.Count)
            //                {
            //                    //f.AddressExpressionIndex++;
            //                    goto FollowFormulaChain;
            //                }
            //            }
            //            if (IsCircularReference(depChain, calcState._stack, f.Id))
            //            {
            //                //Check
            //            }
            //            else
            //            {
            //                depChain.Add(f);
            //            }

            //            if (calcState._stack.Count > 0)
            //            {
            //                f = calcState._stack.Pop();
            //                ws = f._ws;
            //                if (f._formulaEnumerator == null)
            //                {
            //                    goto FollowFormulaChain;
            //                }
            //                else
            //                {
            //                    goto NextFormula;
            //                }
            //            }
            //            return;
            //NextFormula:
            //            var fs = f._formulaEnumerator;
            //            if (f._formulaEnumerator.Next())
            //            {
            //                if (fs.Value == null || fs.Value.ToString().Trim() == "") goto NextFormula;
            //                var id = ExcelCellBase.GetCellId(ws.IndexInList, fs.Row, fs.Column);
            //                if (depChain.processedCells.Contains(id) == false)
            //                {
            //                    depChain.processedCells.Add(id);
            //                    ws.Workbook.FormulaParser.ParsingContext.CurrentCell = new FormulaCellAddress(ws.IndexInList, fs.Row, fs.Column);
            //                    calcState._stack.Push(f);
            //                    if (fs.Value is int ix)
            //                    {
            //                        f = ws._sharedFormulas[ix].GetFormula(fs.Row, fs.Column);
            //                    }

            //                    else
            //                    {
            //                        var s = fs.Value.ToString();
            //                        //compiler
            //                        if (string.IsNullOrEmpty(s)) goto NextFormula;
            //                        f = new Formula(ws, fs.Row, fs.Column, s);
            //                    }
            //                    ws = f._ws;
            //                    goto FollowFormulaChain;
            //                }
            //                else if (IsCircularReference(depChain, calcState._stack, id))
            //                {
            //                    //Check
            //                }

            //                goto NextFormula;
            //            }
            //            f._formulaEnumerator = null;
            //            goto FollowFormulaChain;
        }

        private static bool IsCircularReference(RpnOptimizedDependencyChain depChain, Stack<Formula> stack, ulong Id)
        {
            foreach(var f in stack)
            {
                var fId = ExcelCellBase.GetCellId(f._ws.IndexInList, f.StartRow, f.StartCol);
                if (Id==fId)
                {
                    depChain._circularReferences.Add(Id);
                    //throw Circual Reference.
                    //throw new CircularReferenceException($"Circular reference detected in cell {ExcelCellBase.GetAddress(f.StartRow,f.StartCol)}");
                    return true;
                }
            }
            return false;
        }

        private static bool GetProcessedAddress(RpnOptimizedDependencyChain depChain, ref FormulaRangeAddress address)
        {
            if (depChain.accessedRanges.TryGetValue(address.WorksheetIx, out RangeDictionary wsRd) == false)
            {
                wsRd = new RangeDictionary();
                depChain.accessedRanges.Add(address.WorksheetIx, wsRd);
            }
            return wsRd.Merge(ref address);
        }
        private static bool GetProcessedAddress(RpnOptimizedDependencyChain depChain, int wsIndex, int row, int col)
        {
            if (depChain.accessedRanges.TryGetValue(wsIndex, out RangeDictionary wsRd) == false)
            {
                wsRd = new RangeDictionary();
                depChain.accessedRanges.Add(wsIndex, wsRd);
            }
            return wsRd.Merge(row, col);
        }
    }
}
