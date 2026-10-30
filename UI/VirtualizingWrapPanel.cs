using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Desko.UI
{
    // A wrapping panel that only creates the item containers in view, for fences with hundreds or thousands of
    // items (a portal onto Downloads...). All cells share one size, so the visible range follows from the
    // scroll offset without measuring anything else. Small fences keep the plain WrapPanel.
    internal sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
    {
        public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
            nameof(CellSize), typeof(Size), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(new Size(84, 94), FrameworkPropertyMetadataOptions.AffectsMeasure));

        public Size CellSize
        {
            get => (Size)GetValue(CellSizeProperty);
            set => SetValue(CellSizeProperty, value);
        }

        Size _extent, _viewport;
        Point _offset;
        int _columns = 1;

        protected override Size MeasureOverride(Size available)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            int count = owner?.Items.Count ?? 0;
            var cell = CellSize;
            double width = double.IsInfinity(available.Width) ? cell.Width * 4 : available.Width;
            double height = double.IsInfinity(available.Height) ? cell.Height * 4 : available.Height;

            _columns = Math.Max(1, (int)(width / cell.Width));
            int rows = (count + _columns - 1) / _columns;
            UpdateScrollInfo(new Size(width, height), new Size(width, rows * cell.Height));

            int firstRow = (int)(_offset.Y / cell.Height);
            int lastRow = (int)((_offset.Y + _viewport.Height) / cell.Height);
            int first = Math.Min(count, firstRow * _columns);
            int last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);

            _ = InternalChildren; // the generator is only usable once the children collection exists
            var generator = ItemContainerGenerator;
            if (count > 0 && last >= first)
            {
                var start = generator.GeneratorPositionFromIndex(first);
                int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
                using (generator.StartAt(start, GeneratorDirection.Forward, true))
                {
                    for (int i = first; i <= last; i++, childIndex++)
                    {
                        var child = (UIElement)generator.GenerateNext(out bool isNew);
                        if (isNew)
                        {
                            if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                            else InsertInternalChild(childIndex, child);
                            generator.PrepareItemContainer(child);
                        }
                        child.Measure(cell);
                    }
                }
            }
            CleanUp(first, last);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var generator = ItemContainerGenerator;
            var cell = CellSize;
            for (int i = 0; i < InternalChildren.Count; i++)
            {
                int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                int row = index / _columns, col = index % _columns;
                InternalChildren[i].Arrange(new Rect(col * cell.Width, row * cell.Height - _offset.Y, cell.Width, cell.Height));
            }
            return finalSize;
        }

        // Containers scrolled out of view are recycled through the generator.
        void CleanUp(int first, int last)
        {
            var generator = ItemContainerGenerator;
            for (int i = InternalChildren.Count - 1; i >= 0; i--)
            {
                int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                if (index >= first && index <= last) continue;
                generator.Remove(new GeneratorPosition(i, 0), 1);
                RemoveInternalChildRange(i, 1);
            }
        }

        protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
        {
            if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
            base.OnItemsChanged(sender, args);
        }

        void UpdateScrollInfo(Size viewport, Size extent)
        {
            if (viewport == _viewport && extent == _extent) return;
            _viewport = viewport;
            _extent = extent;
            _offset.Y = Math.Max(0, Math.Min(_offset.Y, _extent.Height - _viewport.Height));
            ScrollOwner?.InvalidateScrollInfo();
        }

        // ---- IScrollInfo (vertical only) ----
        public bool CanHorizontallyScroll { get; set; }
        public bool CanVerticallyScroll { get; set; }
        public double ExtentWidth => _extent.Width;
        public double ExtentHeight => _extent.Height;
        public double ViewportWidth => _viewport.Width;
        public double ViewportHeight => _viewport.Height;
        public double HorizontalOffset => 0;
        public double VerticalOffset => _offset.Y;
        public ScrollViewer? ScrollOwner { get; set; }

        double Line => CellSize.Height / 3;
        public void LineUp() => SetVerticalOffset(_offset.Y - Line);
        public void LineDown() => SetVerticalOffset(_offset.Y + Line);
        public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
        public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
        public void MouseWheelUp() => SetVerticalOffset(_offset.Y - Line * 3);
        public void MouseWheelDown() => SetVerticalOffset(_offset.Y + Line * 3);
        public void LineLeft() { }
        public void LineRight() { }
        public void PageLeft() { }
        public void PageRight() { }
        public void MouseWheelLeft() { }
        public void MouseWheelRight() { }
        public void SetHorizontalOffset(double offset) { }

        public void SetVerticalOffset(double offset)
        {
            offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
            if (offset == _offset.Y) return;
            _offset.Y = offset;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }

        public Rect MakeVisible(Visual visual, Rect rectangle)
        {
            if (visual is not UIElement element) return rectangle;
            int i = InternalChildren.IndexOf(element);
            if (i < 0) return rectangle;
            int index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            double top = index / _columns * CellSize.Height;
            if (top < _offset.Y) SetVerticalOffset(top);
            else if (top + CellSize.Height > _offset.Y + _viewport.Height) SetVerticalOffset(top + CellSize.Height - _viewport.Height);
            return rectangle;
        }

        // Keyboard focus moving to an item that isn't realized yet (Home/End, arrows past the view).
        protected override void BringIndexIntoView(int index)
        {
            double top = index / _columns * CellSize.Height;
            if (top < _offset.Y) SetVerticalOffset(top);
            else if (top + CellSize.Height > _offset.Y + _viewport.Height) SetVerticalOffset(top + CellSize.Height - _viewport.Height);
        }
    }
}
