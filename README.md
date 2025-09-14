# Sierpinski Render

A small Windows Forms application that renders the Sierpinski triangle using the classic "chaos game" method. It is optimized to keep the UI responsive while drawing tens of thousands of points per frame, and includes optional visual tracing of individual steps.

## Requirements
- Windows
- .NET Framework 4.8
- IDE: JetBrains Rider or Microsoft Visual Studio 2019/2022 (or later) with .NET desktop development tools

## Getting Started
1. Open the solution file `SierpinskiRender.sln` in your IDE.
2. Select Debug (or Release) and Any CPU.
3. Build and run the solution. The main form will appear with the canvas and controls at the top.

## Controls and Features
- Start/Stop: Starts or stops the render loop.
- Export: Saves the current image to PNG, JPEG, or BMP. The app clones the current bitmap before saving to avoid any GDI+ locking conflicts.
- Show traces: When enabled, draws a transient overlay showing the chosen triangle vertex (red dot), the midpoint plotted (black dot), and the line from last point to the chosen vertex (lime). The overlay is shown for one paint frame per step to avoid clutter.
- Delay (ns): Optional delay between steps in nanoseconds (useful for stepping/observing trace behavior). Large values will sleep in millisecond chunks for efficiency.
- Zoom: Adjusts the triangle size between 50% and 400% relative to the canvas. Window resizing will re-create the internal image to match the new size.

## How It Works
- The app uses an owner-draw PictureBox: pixels are written into a byte buffer, then copied into a 32bpp ARGB Bitmap using LockBits/UnlockBits.
- Painting is synchronized via a shared lock to avoid "Bitmap region is already locked" exceptions. The UI thread draws the bitmap and (optionally) the overlay under the same lock. Any rare GDI+ paint-time errors are defensively ignored for that frame.
- When not in step mode, the renderer computes large batches of points (e.g., 50,000) before updating the bitmap to balance speed and responsiveness.

## Troubleshooting
- If you see no image, click Start to begin rendering.
- If traces seem to disappear instantly, this is by design: the overlay is a one-shot visual shown for one paint frame per step. Increase the delay to observe it more clearly, or just enable "Show traces" without delay to see frequent one-frame overlays.
- Exported images may appear scaled differently than on screen—export saves the raw canvas bitmap at its current size.

## Project Structure
- `SierpinskiRender/` — Windows Forms project (Program.cs, Form1.cs, Designer and Properties).
- `SierpinskiRender.sln` — Solution file.

## License
No license has been specified for this repository. If you intend to make this code public or redistribute it, consider adding a software license.
