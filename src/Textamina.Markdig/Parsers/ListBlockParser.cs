// Copyright (c) Alexandre Mutel. All rights reserved.
// This file is licensed under the BSD-Clause 2 license. 
// See the license.txt file in the project root for more information.
using System;
using System.Collections.Generic;
using Textamina.Markdig.Helpers;
using Textamina.Markdig.Syntax;

namespace Textamina.Markdig.Parsers
{

    public abstract class ListItemParser
    {
        public char[] OpeningCharacters { get; protected set; }

        public string DefaultOrderedStart { get; protected set; }

        public abstract bool TryParse(BlockProcessor state, out char bulletType, out string orderedStart, out char orderedDelimiter);
    }


    public abstract class OrderedListItemParser : ListItemParser
    {
        protected OrderedListItemParser()
        {
            OrderedDelimiters = new[] { '.', ')' };
        }

        /// <summary>
        /// Gets or sets the ordered delimiters used after a digit/number (by default `.` and `)`)
        /// </summary>
        public char[] OrderedDelimiters { get; set; }

        protected bool ParseDelimiter(char c)
        {
            // Check if we have an ordered delimiter
            for (int i = 0; i < OrderedDelimiters.Length; i++)
            {
                if (OrderedDelimiters[i] == c)
                {
                    return true;
                }
            }
            return false;
        }
    }


    public class UnorderedListItemParser : ListItemParser
    {
        public UnorderedListItemParser()
        {
            OpeningCharacters = new [] {'-', '+', '*'};
        }

        public override bool TryParse(BlockProcessor state, out char bulletType, out string orderedStart, out char orderedDelimiter)
        {
            orderedStart = null;
            orderedDelimiter = (char)0;
            bulletType = state.CurrentChar;
            return true;
        }
    }


    public class NumberedListItemParser : OrderedListItemParser
    {
        public NumberedListItemParser()
        {
            OpeningCharacters = new char[10];
            for (int i = 0; i < 10; i++)
            {
                OpeningCharacters[i] = (char) ('0' + i);
            }
            DefaultOrderedStart = "1";
        }

        public override bool TryParse(BlockProcessor state, out char bulletType, out string orderedStart, out char orderedDelimiter)
        {
            bulletType = (char)0;
            orderedDelimiter = (char) 0;
            var c = state.CurrentChar;
            orderedStart = null;

            int countDigit = 0;
            int startChar = -1;
            int endChar = 0;
            while (c.IsDigit())
            {
                endChar = state.Start;
                // Trim left 0
                if (startChar < 0 && c != '0')
                {
                    startChar = endChar;
                }
                c = state.NextChar();
                countDigit++;
            }
            if (startChar < 0)
            {
                startChar = endChar;
            }

            // Note that ordered list start numbers must be nine digits or less:
            if (countDigit > 9 || !ParseDelimiter(c))
            {
                return false;
            }

            orderedStart = state.Line.Text.Substring(startChar, endChar - startChar + 1);
            orderedDelimiter = c;
            bulletType = '1';
            return true;
        }
    }


    /// <summary>
    /// A parser for a list block and list item block.
    /// </summary>
    /// <seealso cref="Textamina.Markdig.Parsers.BlockParser" />
    public class ListBlockParser : BlockParser
    {
        private CharacterMap<ListItemParser> mapItemParsers;

        /// <summary>
        /// Initializes a new instance of the <see cref="ListBlockParser"/> class.
        /// </summary>
        public ListBlockParser()
        {
            //OpeningCharacters = new[] {'-', '+', '*', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'};

            ItemParsers = new List<ListItemParser>()
            {
                new UnorderedListItemParser(),
                new NumberedListItemParser()
            };
        }

        /// <summary>
        /// Gets the parsers for items.
        /// </summary>
        public List<ListItemParser> ItemParsers { get; }

        public override void Initialize(BlockProcessor processor)
        {
            var tempMap = new Dictionary<char, ListItemParser>();

            foreach (var itemParser in ItemParsers)
            {
                foreach (var openingCharacter in itemParser.OpeningCharacters)
                {
                    if (tempMap.ContainsKey(openingCharacter))
                    {
                        throw new InvalidOperationException(
                            $"A list item parser with the same opening character `{openingCharacter}` is already registered");
                    }
                    tempMap.Add(openingCharacter, itemParser);
                }
            }
            mapItemParsers = new CharacterMap<ListItemParser>(tempMap);
        }

        public override BlockState TryOpen(BlockProcessor processor)
        {
            // When both a thematic break and a list item are possible
            // interpretations of a line, the thematic break takes precedence
            var thematicParser = ThematicBreakParser.Default;
            if (thematicParser.HasOpeningCharacter(processor.CurrentChar))
            {
                var result = thematicParser.TryOpen(processor);
                if (result.IsBreak())
                {
                    return result;
                }
            }

            return TryParseListItem(processor, null);
        }

        public override BlockState TryContinue(BlockProcessor processor, Block block)
        {
            if (block is ListBlock && processor.NextContinue is ListItemBlock)
            {
                // We try to match only on item block if the ListBlock
                return BlockState.Skip;
            }

            // When both a thematic break and a list item are possible
            // interpretations of a line, the thematic break takes precedence
            BlockState result;
            var thematicParser = ThematicBreakParser.Default;
            if (thematicParser.HasOpeningCharacter(processor.CurrentChar))
            {
                result = thematicParser.TryOpen(processor);
                if (result.IsBreak())
                {
                    // TODO: We remove the thematic break, as it will be created later, but this is inefficient, try to find another way
                    processor.NewBlocks.Pop();
                    return BlockState.None;
                }
            }

            // 5.2 List items 
            // TODO: Check with specs, it is not clear that list marker or bullet marker must be followed by at least 1 space

            // If we have already a ListItemBlock, we are going to try to append to it
            var listItem = block as ListItemBlock;
            result = BlockState.None;
            if (listItem != null)
            {
                result = TryContinueListItem(processor, listItem);
            }

            if (result == BlockState.None)
            {
                result = TryParseListItem(processor, block);
            }

            return result;
        }

        private BlockState TryContinueListItem(BlockProcessor state, ListItemBlock listItem)
        {
            var list = (ListBlock)listItem.Parent;

            // Allow all blanks lines if the last block is a fenced code block
            // Allow 1 blank line inside a list
            // If > 1 blank line, terminate this list
            var isBlankLine = state.IsBlankLine;

            var isCurrentBlockBreakable = state.CurrentBlock != null && state.CurrentBlock.IsBreakable;
            if (isBlankLine)
            {
                if (isCurrentBlockBreakable)
                {
                    if (!(state.NextContinue is ListBlock))
                    {
                        list.CountAllBlankLines++;
                        listItem.Add(new BlankLineBlock());
                    }
                    list.CountBlankLinesReset++;
                }

                if (list.CountBlankLinesReset > 1)
                {
                    return BlockState.BreakDiscard;
                }

                if (list.CountBlankLinesReset == 1 && listItem.ColumnWidth < 0)
                {
                    state.Close(listItem);

                    // Leave the list open
                    list.IsOpen = true;
                    return BlockState.Continue;
                }

                return BlockState.Continue;
            }

            list.CountBlankLinesReset = 0;

            int columWidth = listItem.ColumnWidth;
            if (columWidth < 0)
            {
                columWidth = -columWidth;
            }

            // TODO: Handle code indent
            if (state.Indent >= columWidth)
            {
                if (state.Indent > columWidth && state.IsCodeIndent)
                {
                    state.GoToColumn(columWidth);
                }

                return BlockState.Continue;
            }

            return BlockState.None;
        }

        private BlockState TryParseListItem(BlockProcessor state, Block block)
        {
            // If we have a code indent and we are not in a ListItem, early exit
            if (!(block is ListItemBlock) && state.IsCodeIndent)
            {
                return BlockState.None;
            }

            var initColumnBeforeIndent = state.ColumnBeforeIndent;
            var initColumn = state.Column;

            var c = state.CurrentChar;
            var itemParser = mapItemParsers[c];
            bool isOrdered = itemParser is OrderedListItemParser;
            if (itemParser == null)
            {
                return BlockState.None;
            }

            // Try to parse the list item
            char bulletType;
            string orderedStart;
            char orderedDelimiter;
            if (!itemParser.TryParse(state, out bulletType, out orderedStart, out orderedDelimiter))
            {
                // Reset to an a start position
                state.GoToColumn(initColumn);
                return BlockState.None;
            }

            // Skip Bullet or '.' or ')'
            c = state.NextChar();

            // Item starting with a blank line
            int columnWidth;

            // Do we have a blank line right after the bullet?
            if (c == '\0')
            {
                // Use a negative number to store the number of expected chars
                columnWidth = -(state.Column - initColumnBeforeIndent + 1);
            }
            else
            {
                if (!c.IsSpaceOrTab())
                {
                    state.GoToColumn(initColumn);
                    return BlockState.None;
                }

                // We require at least one char
                state.NextColumn();

                // Parse the following indent
                state.RestartIndent();
                var columnBeforeIndent = state.Column;
                state.ParseIndent();

                if (state.IsCodeIndent)
                {
                    state.GoToColumn(columnBeforeIndent);
                }

                // Number of spaces required for the following content to be part of this list item
                columnWidth = state.Column - initColumnBeforeIndent;
            }

            var newListItem = new ListItemBlock(this)
            {
                Column = initColumn,
                ColumnWidth = columnWidth
            };
            state.NewBlocks.Push(newListItem);

            var currentListItem = block as ListItemBlock;
            var currentParent = block as ListBlock ?? (ListBlock)currentListItem?.Parent;

            if (currentParent != null)
            {
                // If we have a new list item, close the previous one
                if (currentListItem != null)
                {
                    state.Close(currentListItem);
                }

                // Reset the list if it is a new list or a new type of bullet
                if (currentParent.IsOrdered != isOrdered ||
                    (isOrdered && currentParent.OrderedDelimiter != orderedDelimiter) ||
                    (!isOrdered && currentParent.BulletType != bulletType)
                    )
                {
                    state.Close(currentParent);
                    currentParent = null;
                }
            }

            if (currentParent == null)
            {
                var newList = new ListBlock(this)
                {
                    Column = initColumn,
                    IsOrdered = isOrdered,
                    BulletType = bulletType,
                    OrderedDelimiter = orderedDelimiter,
                    DefaultOrderedStart = itemParser.DefaultOrderedStart,
                    OrderedStart = orderedStart,
                };
                state.NewBlocks.Push(newList);
            }

            return BlockState.Continue;
        }

        public override bool Close(BlockProcessor processor, Block blockToClose)
        {
            var listBlock = blockToClose as ListBlock;

            // Process only if we have blank lines
            if (listBlock == null || listBlock.CountAllBlankLines <= 0)
            {
                return true;
            }

            // TODO: This code is UGLY and WAY TOO LONG, simplify!
            bool isLastListItem = true;
            for (int listIndex = listBlock.Count - 1; listIndex >= 0; listIndex--)
            {
                var block = listBlock[listIndex];
                var listItem = (ListItemBlock) block;
                bool isLastElement = true;
                for (int i = listItem.Count - 1; i >= 0; i--)
                {
                    var item = listItem[i];
                    if (item is BlankLineBlock)
                    {
                        if ((isLastElement &&  listIndex < listBlock.Count - 1) || (listItem.Count > 2 && (i > 0 && i < (listItem.Count - 1))))
                        {
                            listBlock.IsLoose = true;
                        }

                        if (isLastElement && isLastListItem)
                        {
                            // Inform the outer list that we have a blank line
                            var parentListItemBlock = listBlock.Parent as ListItemBlock;
                            if (parentListItemBlock != null)
                            {
                                var parentList = (ListBlock) parentListItemBlock.Parent;

                                parentList.CountAllBlankLines++;
                                parentListItemBlock.Add(new BlankLineBlock());
                            }
                        }

                        listItem.RemoveAt(i);

                        // If we have remove all blank lines, we can exit
                        listBlock.CountAllBlankLines--;
                        if (listBlock.CountAllBlankLines == 0)
                        {
                            break;
                        }
                    }
                    isLastElement = false;
                }
                isLastListItem = false;
            }

            return true;
        }
    }
}