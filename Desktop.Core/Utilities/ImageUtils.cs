﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Remotely.Desktop.Core.Utilities
{
    public class ImageUtils
    {
        public static ImageCodecInfo JpegEncoder { get; } = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x => x.FormatID == ImageFormat.Jpeg.Guid);
        public static ImageCodecInfo GifEncoder { get; } = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x => x.FormatID == ImageFormat.Gif.Guid);

        public static byte[] EncodeBitmap(Bitmap bitmap, EncoderParameters encoderParams)
        {
            
            using var ms = new MemoryStream();
            bitmap.Save(ms, JpegEncoder, encoderParams);
            return ms.ToArray();
        }

        public static byte[] EncodeGif(Bitmap diffImage)
        {
            diffImage.MakeTransparent(Color.FromArgb(0, 0, 0, 0));
            using var ms = new MemoryStream();
            diffImage.Save(ms, ImageFormat.Gif);
            return ms.ToArray();
        }

        public static List<Rectangle> GetDiffAreas(Bitmap currentFrame, Bitmap previousFrame, bool captureFullscreen)
        {
            var changes = new List<Rectangle>();

            if (currentFrame == null || previousFrame == null)
            {
                return changes;
            }

            if (captureFullscreen)
            {
                changes.Add(new Rectangle(new Point(0, 0), currentFrame.Size));
                return changes;
            }
            if (currentFrame.Height != previousFrame.Height || currentFrame.Width != previousFrame.Width)
            {
                throw new Exception("Bitmaps are not of equal dimensions.");
            }
            if (currentFrame.PixelFormat != previousFrame.PixelFormat)
            {
                throw new Exception("Bitmaps are not the same format.");
            }
            var width = currentFrame.Width;
            var height = currentFrame.Height;
            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;

            BitmapData bd1 = null;
            BitmapData bd2 = null;

            try
            {
                bd1 = previousFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, currentFrame.PixelFormat);
                bd2 = currentFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, previousFrame.PixelFormat);

                var bytesPerPixel = Bitmap.GetPixelFormatSize(currentFrame.PixelFormat) / 8;
                var totalSize = bd1.Height * bd1.Width * bytesPerPixel;

                unsafe
                {
                    byte* scan1 = (byte*)bd1.Scan0.ToPointer();
                    byte* scan2 = (byte*)bd2.Scan0.ToPointer();

                    var changeOnCurrentRow = false;
                    var changeOnPreviousRow = false;

                    for (var row = 0; row < height; row++)
                    {
                        changeOnPreviousRow = changeOnCurrentRow;
                        changeOnCurrentRow = false;

                        for (var column = 0; column < width; column++)
                        {
                            var index = (row * width * bytesPerPixel) + (column * bytesPerPixel);

                            byte* data1 = scan1 + index;
                            byte* data2 = scan2 + index;

                            if (data1[0] != data2[0] ||
                                data1[1] != data2[1] ||
                                data1[2] != data2[2] ||
                                data1[3] != data2[3])
                            {
                                changeOnCurrentRow = true;

                                if (row < top)
                                {
                                    top = row;
                                }
                                if (row > bottom)
                                {
                                    bottom = row;
                                }
                                if (column < left)
                                {
                                    left = column;
                                }
                                if (column > right)
                                {
                                    right = column;
                                }
                            }

                        }
                        if (!changeOnCurrentRow &&
                            changeOnPreviousRow &&
                            left <= right &&
                            top <= bottom)
                        {
                            AddChangeToList(changes, left, top, right, bottom, width, height);

                            left = int.MaxValue;
                            top = int.MaxValue;
                            right = int.MinValue;
                            bottom = int.MinValue;
                        }
                    }
                    if (changeOnCurrentRow &&
                        left <= right &&
                        top <= bottom)
                    {
                        AddChangeToList(changes, left, top, right, bottom, width, height);
                    }
                }

                return changes;
            }
            catch
            {
                return changes;
            }
            finally
            {
                currentFrame.UnlockBits(bd1);
                previousFrame.UnlockBits(bd2);
            }
        }

        public static ICollection<Rectangle> GetDiffAreas2(Bitmap currentFrame, Bitmap previousFrame, bool captureFullscreen)
        {
            if (currentFrame == null || previousFrame == null)
            {
                return Array.Empty<Rectangle>();
            }

            if (captureFullscreen)
            {
                return new Rectangle[] { new Rectangle(new Point(0, 0), currentFrame.Size) };
            }

            if (currentFrame.Height != previousFrame.Height || currentFrame.Width != previousFrame.Width)
            {
                throw new Exception("Bitmaps are not of equal dimensions.");
            }
            if (currentFrame.PixelFormat != previousFrame.PixelFormat)
            {
                throw new Exception("Bitmaps are not the same format.");
            }

            var width = currentFrame.Width;
            var height = currentFrame.Height;

            BitmapData bd1 = null;
            BitmapData bd2 = null;

            var bytesPerPixel = Bitmap.GetPixelFormatSize(currentFrame.PixelFormat) / 8;
            var numberOfPixels = width * height;
            var totalSize = numberOfPixels * bytesPerPixel;
            var changes = new ConcurrentQueue<Rectangle>();
            try
            {
                bd1 = previousFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, currentFrame.PixelFormat);
                bd2 = currentFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, previousFrame.PixelFormat);

                unsafe
                {

                    byte* scan1 = (byte*)bd1.Scan0.ToPointer();
                    byte* scan2 = (byte*)bd2.Scan0.ToPointer();


                    var gridColumnWidth = width % 8 == 0 ? width / 8 :
                        width % 2 == 0 ? width / 2 :
                        width;


                    var gridRowHeight = height % 9 == 0 ? height / 9 :
                        height % 10 == 0 ? height / 10 :
                        height % 4 == 0 ? height / 4 :
                        height;

                    var gridColumns = Enumerable.Range(0, width).Where(i => i % gridColumnWidth == 0);
                    var gridRows = Enumerable.Range(0, height).Where(i => i % gridRowHeight == 0);

                    
                    Parallel.ForEach(gridColumns, gridColumn =>
                    {
                        Parallel.ForEach(gridRows, gridRow =>
                        {
                            int left = int.MaxValue;
                            int top = int.MaxValue;
                            int right = int.MinValue;
                            int bottom = int.MinValue;

                            for (var row = 0; row < gridRowHeight; row++)
                            {
                                for (var col = 0; col < gridColumnWidth; col++)
                                {
                                    var pixelLeft = gridColumn + col;
                                    var pixelTop = gridRow + row;


                                    var rowIndex = pixelTop * width * bytesPerPixel;

                                    var columnIndex = pixelLeft * bytesPerPixel;

                                    var i = rowIndex + columnIndex;

                                    byte* data1 = scan1 + i;
                                    byte* data2 = scan2 + i;

                                    if (data1[0] != data2[0] ||
                                        data1[1] != data2[1] ||
                                        data1[2] != data2[2] ||
                                        data1[3] != data2[3])
                                    {

                                        if (pixelTop < top)
                                        {
                                            top = pixelTop;
                                        }
                                        if (pixelTop > bottom)
                                        {
                                            bottom = pixelTop;
                                        }
                                        if (pixelLeft < left)
                                        {
                                            left = pixelLeft;
                                        }
                                        if (pixelLeft > right)
                                        {
                                            right = pixelLeft;
                                        }
                                    }
                                }
                            }

                            if (left <= right && top <= bottom)
                            {
                                AddChangeToList(changes, left, top, right, bottom, width, height);
                            }
                        });
                    });
                    var finishedAreas = new List<Rectangle>();
                    changes.ToList().ForEach(x =>
                    {
                        var neighborIndex = finishedAreas.FindIndex(y =>
                        {
                            return new Rectangle(y.X - 1, y.Y - 1, y.Width + 2, y.Height + 2).IntersectsWith(x);
                        });

                        if (neighborIndex > -1 && !finishedAreas[neighborIndex].IsEmpty)
                        {
                            finishedAreas[neighborIndex] = Rectangle.Union(finishedAreas[neighborIndex], x);
                        }
                        else
                        {
                            finishedAreas.Add(x);
                        }
                    });
                    return finishedAreas;
                }
            }
            catch
            {
                return changes.ToArray();
            }
            finally
            {
                currentFrame.UnlockBits(bd1);
                previousFrame.UnlockBits(bd2);
            }
        }

        public static ICollection<Rectangle> GetDiffAreas3(Bitmap currentFrame, Bitmap previousFrame, bool captureFullscreen)
        {
            if (currentFrame == null || previousFrame == null)
            {
                return Array.Empty<Rectangle>();
            }

            if (captureFullscreen)
            {
                return new Rectangle[] { new Rectangle(new Point(0, 0), currentFrame.Size) };
            }

            if (currentFrame.Height != previousFrame.Height || currentFrame.Width != previousFrame.Width)
            {
                throw new Exception("Bitmaps are not of equal dimensions.");
            }
            if (currentFrame.PixelFormat != previousFrame.PixelFormat)
            {
                throw new Exception("Bitmaps are not the same format.");
            }

            var width = currentFrame.Width;
            var height = currentFrame.Height;

            BitmapData bd1 = null;
            BitmapData bd2 = null;

            var bytesPerPixel = Bitmap.GetPixelFormatSize(currentFrame.PixelFormat) / 8;
            var numberOfPixels = width * height;
            var totalSize = numberOfPixels * bytesPerPixel;
            var changes = new ConcurrentQueue<Rectangle>();
            try
            {
                bd1 = previousFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, currentFrame.PixelFormat);
                bd2 = currentFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, previousFrame.PixelFormat);

                unsafe
                {

                    byte* scan1 = (byte*)bd1.Scan0.ToPointer();
                    byte* scan2 = (byte*)bd2.Scan0.ToPointer();

                    var gridRowHeight = height % 9 == 0 ? height / 9 :
                        height % 10 == 0 ? height / 10 :
                        height % 4 == 0 ? height / 4 :
                        height;

                    var gridRows = Enumerable.Range(0, height).Where(i => i % gridRowHeight == 0);

                    Parallel.ForEach(gridRows, gridRow =>
                    {
                        int left = int.MaxValue;
                        int top = int.MaxValue;
                        int right = int.MinValue;
                        int bottom = int.MinValue;

                        for (var row = 0; row < gridRowHeight; row++)
                        {
                            for (var col = 0; col < width; col++)
                            {
                                var pixelLeft = col;
                                var pixelTop = gridRow + row;


                                var rowIndex = pixelTop * width * bytesPerPixel;

                                var columnIndex = pixelLeft * bytesPerPixel;

                                var i = rowIndex + columnIndex;

                                byte* data1 = scan1 + i;
                                byte* data2 = scan2 + i;

                                if (data1[0] != data2[0] ||
                                    data1[1] != data2[1] ||
                                    data1[2] != data2[2] ||
                                    data1[3] != data2[3])
                                {

                                    if (pixelTop < top)
                                    {
                                        top = pixelTop;
                                    }
                                    if (pixelTop > bottom)
                                    {
                                        bottom = pixelTop;
                                    }
                                    if (pixelLeft < left)
                                    {
                                        left = pixelLeft;
                                    }
                                    if (pixelLeft > right)
                                    {
                                        right = pixelLeft;
                                    }
                                }
                            }
                        }
                        if (left <= right && top <= bottom)
                        {
                            AddChangeToList(changes, left, top, right, bottom, width, height);
                        }
                    });

                    return changes.ToArray();
                }
            }
            catch
            {
                return changes.ToArray();
            }
            finally
            {
                currentFrame.UnlockBits(bd1);
                previousFrame.UnlockBits(bd2);
            }
        }



        public static Bitmap GetImageDiff(Bitmap currentFrame, Bitmap previousFrame, bool captureFullscreen, out bool hadChanges)
        {
            hadChanges = false;
            if (currentFrame is null || previousFrame is null)
            {
                hadChanges = false;
                return null;
            }
            if (captureFullscreen)
            {
                hadChanges = true;
                return (Bitmap)currentFrame.Clone();
            }

            if (currentFrame.Height != previousFrame.Height || currentFrame.Width != previousFrame.Width)
            {
                throw new Exception("Bitmaps are not of equal dimensions.");
            }
            if (!Bitmap.IsAlphaPixelFormat(currentFrame.PixelFormat) || !Bitmap.IsAlphaPixelFormat(previousFrame.PixelFormat) ||
                !Bitmap.IsCanonicalPixelFormat(currentFrame.PixelFormat) || !Bitmap.IsCanonicalPixelFormat(previousFrame.PixelFormat))
            {
                throw new Exception("Bitmaps must be 32 bits per pixel and contain alpha channel.");
            }
            var width = currentFrame.Width;
            var height = currentFrame.Height;

            var mergedFrame = new Bitmap(width, height);

            var bd1 = previousFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, currentFrame.PixelFormat);
            var bd2 = currentFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, previousFrame.PixelFormat);
            var bd3 = mergedFrame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, currentFrame.PixelFormat);

            try
            {
                var bytesPerPixel = Bitmap.GetPixelFormatSize(currentFrame.PixelFormat) / 8;
                var totalSize = bd1.Height * bd1.Width * bytesPerPixel;

                unsafe
                {
                    byte* scan1 = (byte*)bd1.Scan0.ToPointer();
                    byte* scan2 = (byte*)bd2.Scan0.ToPointer();
                    byte* scan3 = (byte*)bd3.Scan0.ToPointer();

                    for (int counter = 0; counter < totalSize - bytesPerPixel; counter += bytesPerPixel)
                    {
                        byte* data1 = scan1 + counter;
                        byte* data2 = scan2 + counter;
                        byte* data3 = scan3 + counter;

                        if (data1[0] != data2[0] ||
                            data1[1] != data2[1] ||
                            data1[2] != data2[2] ||
                            data1[3] != data2[3])
                        {
                            hadChanges = true;
                            data3[0] = data2[0];
                            data3[1] = data2[1];
                            data3[2] = data2[2];
                            data3[3] = data2[3];
                        }
                    }
                }


                return mergedFrame;

            }
            catch
            {
                return mergedFrame;
            }
            finally
            {
                previousFrame.UnlockBits(bd1);
                currentFrame.UnlockBits(bd2);
                mergedFrame.UnlockBits(bd3);
            }
        }

        private static void AddChangeToList(List<Rectangle> changes, int left, int top, int right, int bottom, int width, int height)
        {
            // Bounding box is valid.  Padding is necessary to prevent artifacts from
            // moving windows.
            left = Math.Max(left - 5, 0);
            top = Math.Max(top - 5, 0);
            right = Math.Min(right + 5, width);
            bottom = Math.Min(bottom + 5, height);

            changes.Add(new Rectangle(left, top, right - left, bottom - top));
        }

        private static void AddChangeToList(ConcurrentQueue<Rectangle> changes, int left, int top, int right, int bottom, int width, int height)
        {
            // Bounding box is valid.  Padding is necessary to prevent artifacts from
            // moving windows.
            left = Math.Max(left - 5, 0);
            top = Math.Max(top - 5, 0);
            right = Math.Min(right + 5, width);
            bottom = Math.Min(bottom + 5, height);

            changes.Enqueue(new Rectangle(left, top, right - left, bottom - top));
        }
    }
}
