using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using SS14.Client.GameObjects;
using SS14.Client.Graphics.ClientEye;
using SS14.Client.Graphics.Drawing;
using SS14.Client.Graphics.Overlays;
using SS14.Client.ResourceManagement;
using SS14.Shared.Log;
using SS14.Shared.Maths;
using SS14.Shared.Utility;

namespace SS14.Client.Graphics.Clyde
{
    internal partial class Clyde
    {
        // ReSharper disable once IdentifierTypo
        private static readonly (uint, string) DbgGroupSSBW = (1, "Overlays: Screen Space Below World");
        private static readonly (uint, string) DbgGroupGrids = (2, "Grids");
        private static readonly (uint, string) DbgGroupEntities = (3, "Entities");
        private static readonly (uint, string) DbgGroupUI = (4, "User Interface");

        /// <summary>
        ///     The current model matrix we would use.
        ///     Necessary since certain drawing operations mess with it still.
        /// </summary>
        private Matrix3 _currentModelMatrix = Matrix3.Identity;

        private bool _batchModelMatricesNeedsUpdate = true;

        /// <summary>
        ///     Are we current rendering screen space or world space? Some code works differently between the two.
        /// </summary>
        private CurrentSpace _currentSpace;

        // The minimum amount of consecutive textures that have to be batch-able before we actually bother to batch them.
        // BufferSubData has a non zero overhead and a few draw calls is much better.
        private const int TextureBatchThreshold = 8;

        // The max amount of vertices in one batch. Each quad is 4 vertices so...
        private const ushort MaxBatchVertices = 16333 * 4;

        // (2**16/4)-1, so that we can use ushort indices in the index buffer
        // and leave open the primitive restart index (65535).
        private const ushort MaxBatchQuads = 16333;

        private readonly Vertex2D[] BatchVertexData = new Vertex2D[MaxBatchVertices];

        // Need 5 indices per quad: 4 to draw the quad with triangle strips and another one as primitive restart.
        private readonly ushort[] BatchIndexData = new ushort[MaxBatchQuads * 5];

        private readonly RefList<BatchCommand> BatchBuffer = new RefList<BatchCommand>();
        private readonly RefList<Matrix3> BatchModelMatrices = new RefList<Matrix3>();

        private int? BatchingTexture;

        // We break batching when modulate changes too.
        // This simplifies the rendering and catches most cases for now.
        // For example all the walls have a single color.
        // Yes this can be optimized later.
        private Color? BatchingModulate;

        private RenderHandle _renderHandle;

        /// <summary>
        ///     If true, re-allocate buffer objects with BufferData instead of using BufferSubData.
        /// </summary>
        private bool _reallocateBuffers = false;

        /// <summary>
        ///     Log when the command pools go dry and we need to re-allocate objects.
        /// </summary>
        private bool _logPoolOverdraw = false;

        private bool _isScissoring = false;

        public void Render(FrameEventArgs args)
        {
            if (GameController.Mode != GameController.DisplayMode.OpenGL)
            {
                return;
            }

            GL.ClearColor(0, 0, 0, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);

            Vertex2DProgram.Use();

            _currentModelMatrix = Matrix3.Identity;
            _batchModelMatricesNeedsUpdate = true;

            // We hand out this handle so external code can generate command lists for us.
            _renderHandle._drawingHandles.Clear();
            _currentSpace = CurrentSpace.ScreenSpace;

            // Screen view matrix is identity. Easy huh.
            var viewMatrixScreen = Matrix3.Identity;

            // Screen projection matrix.
            var projMatrixScreen = Matrix3.Identity;
            projMatrixScreen.R0C0 = 2f / _window.Width;
            projMatrixScreen.R1C1 = -2f / _window.Height;
            projMatrixScreen.R0C2 = -1;
            projMatrixScreen.R1C2 = 1;

            var combinedMatricesScreen = new ProjViewMatrices(projMatrixScreen, viewMatrixScreen);

            ProjViewUBO.Use();
            if (_reallocateBuffers)
            {
                ProjViewUBO.Reallocate(combinedMatricesScreen);
            }
            else
            {
                ProjViewUBO.WriteSubData(combinedMatricesScreen);
            }

            if (_drawingSplash)
            {
                _displaySplash(_renderHandle);
                _flushRenderHandle(_renderHandle);
                _window.SwapBuffers();
                return;
            }

            var eye = _eyeManager.CurrentEye;

            // World projection matrix.
            var projMatrixWorld = Matrix3.Identity;
            projMatrixWorld.R0C0 = EyeManager.PIXELSPERMETER * 2f / _window.Width;
            projMatrixWorld.R1C1 = EyeManager.PIXELSPERMETER * 2f / _window.Height;

            // World view matrix.
            var viewMatrixWorld = Matrix3.Identity;
            viewMatrixWorld.R0C0 = 1 / eye.Zoom.X;
            viewMatrixWorld.R1C1 = 1 / eye.Zoom.Y;
            viewMatrixWorld.R0C2 = -eye.Position.X / eye.Zoom.X;
            viewMatrixWorld.R1C2 = -eye.Position.Y / eye.Zoom.Y;

            var combinedMatricesWorld = new ProjViewMatrices(projMatrixWorld, viewMatrixWorld);

            _pushDebugGroupMaybe(DbgGroupSSBW);

            // Render ScreenSpaceBelowWorld overlays.
            foreach (var overlay in _overlayManager.AllOverlays
                .Where(o => o.Space == OverlaySpace.ScreenSpaceBelowWorld)
                .OrderBy(o => o.ZIndex))
            {
                overlay.OpenGLRender(_renderHandle);
            }

            _flushRenderHandle(_renderHandle);

            _popDebugGroupMaybe();

            _pushDebugGroupMaybe(DbgGroupGrids);

            _currentSpace = CurrentSpace.WorldSpace;
            // Render grids. Very hardcoded right now.
            var map = _eyeManager.CurrentMap;

            var tex = _resourceCache.GetResource<TextureResource>("/Textures/Tiles/floor_steel.png");
            var loadedTex = _loadedTextures[((OpenGLTexture) tex.Texture).OpenGLTextureId];
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, loadedTex.OpenGLObject.Handle);

            GL.BindVertexArray(BatchVAO.Handle);

            ProjViewUBO.Use();
            if (_reallocateBuffers)
            {
                ProjViewUBO.Reallocate(combinedMatricesWorld);
            }
            else
            {
                ProjViewUBO.WriteSubData(combinedMatricesWorld);
            }

            GL.Enable(EnableCap.PrimitiveRestart);
            GL.PrimitiveRestartIndex(ushort.MaxValue);

            Vertex2DProgram.Use();
            foreach (var grid in _mapManager.GetMap(map).GetAllGrids())
            {
                ushort nth = 0;
                var model = Matrix3.Identity;
                model.R0C2 = grid.WorldPosition.X;
                model.R1C2 = grid.WorldPosition.Y;
                Vertex2DProgram.SetUniform(UniModelMatrix, model);

                foreach (var tileRef in grid.GetAllTiles())
                {
                    var vIdx = nth * 4;
                    BatchVertexData[vIdx + 0] = new Vertex2D(tileRef.X + 1, tileRef.Y, 1, 0, 0);
                    BatchVertexData[vIdx + 1] = new Vertex2D(tileRef.X, tileRef.Y, 0, 0, 0);
                    BatchVertexData[vIdx + 2] = new Vertex2D(tileRef.X + 1, tileRef.Y - 1f, 1, 1, 0);
                    BatchVertexData[vIdx + 3] = new Vertex2D(tileRef.X, tileRef.Y - 1f, 0, 1, 0);
                    var nIdx = nth * 5;
                    var tIdx = (ushort) (nth * 4);
                    BatchIndexData[nIdx + 0] = tIdx;
                    BatchIndexData[nIdx + 1] = (ushort) (tIdx + 1);
                    BatchIndexData[nIdx + 2] = (ushort) (tIdx + 2);
                    BatchIndexData[nIdx + 3] = (ushort) (tIdx + 3);
                    // Use primitive restart to shave off one single index per quad.
                    // Whew.
                    BatchIndexData[nIdx + 4] = ushort.MaxValue;
                    if (++nth >= MaxBatchQuads)
                    {
                        throw new NotImplementedException("Can't render grids that are that big yet sorry.");
                    }
                }

                if (nth == 0)
                {
                    continue;
                }

                BatchVBO.Use();
                BatchEBO.Use();
                var vertexData = new Span<Vertex2D>(BatchVertexData, 0, nth * 4);
                var indexData = new Span<ushort>(BatchIndexData, 0, nth * 5);
                if (_reallocateBuffers)
                {
                    BatchVBO.Reallocate(vertexData);
                    BatchEBO.Reallocate(indexData);
                }
                else
                {
                    BatchVBO.WriteSubData(vertexData);
                    BatchEBO.WriteSubData(indexData);
                }

                GL.DrawElements(PrimitiveType.TriangleStrip, nth * 5, DrawElementsType.UnsignedShort, 0);
            }

            GL.Disable(EnableCap.PrimitiveRestart);

            _popDebugGroupMaybe();

            _pushDebugGroupMaybe(DbgGroupEntities);

            // Use a SINGLE drawing handle for all entities.
            var drawingHandle = _renderHandle.CreateHandleWorld();

            var entityList = new List<SpriteComponent>(100);

            // Draw entities.
            foreach (var entity in _entityManager.GetEntities())
            {
                if (!entity.Transform.IsMapTransform || entity.Transform.MapID != map ||
                    !entity.TryGetComponent(out SpriteComponent sprite))
                {
                    continue;
                }

                entityList.Add(sprite);
            }

            entityList.Sort((a, b) => ((int) a.DrawDepth).CompareTo((int) b.DrawDepth));

            foreach (var entity in entityList)
            {
                entity.OpenGLRender(drawingHandle);
            }

            _flushRenderHandle(_renderHandle);

            _popDebugGroupMaybe();

            _pushDebugGroupMaybe(DbgGroupUI);

            ProjViewUBO.Use();
            if (_reallocateBuffers)
            {
                ProjViewUBO.Reallocate(combinedMatricesScreen);
            }
            else
            {
                ProjViewUBO.WriteSubData(combinedMatricesScreen);
            }

            // Render UI.
            _currentSpace = CurrentSpace.ScreenSpace;
            _userInterfaceManager.Render(_renderHandle);

            _flushRenderHandle(_renderHandle);

            _popDebugGroupMaybe();

            _window.SwapBuffers();
        }

        private void _processCommandList(RenderCommandList list)
        {
            foreach (ref var command in list.RenderCommands)
            {
                switch (command.Type)
                {
                    case RenderCommandType.Texture:
                        _drawCommandTexture(ref command, true, ref _currentModelMatrix);
                        break;
                    case RenderCommandType.Transform:
                        _currentModelMatrix = command.Matrix;
                        _batchModelMatricesNeedsUpdate = true;
                        break;
                    case RenderCommandType.Scissor:
                        _flushBatchBuffer();
                        var oldIsScissoring = _isScissoring;
                        _isScissoring = command.EnableScissor;
                        if (_isScissoring)
                        {
                            if (!oldIsScissoring)
                            {
                                GL.Enable(EnableCap.ScissorTest);
                            }

                            ref var s = ref command.Scissor;
                            // Don't forget to flip it, these coordinates have bottom left as origin.
                            GL.Scissor(s.Left, _window.Height - s.Bottom, s.Width, s.Height);
                        }
                        else if (oldIsScissoring)
                        {
                            GL.Disable(EnableCap.ScissorTest);
                        }

                        break;
                }
            }
        }

        private void _drawCommandTexture(
            ref RenderCommand renderCommandTexture,
            bool doBatching,
            ref Matrix3 modelMatrix)
        {
            if (doBatching)
            {
                if (BatchingTexture.HasValue)
                {
                    DebugTools.Assert(BatchingModulate.HasValue);
                    if (BatchingTexture.Value != renderCommandTexture.TextureId ||
                        BatchingModulate.Value != renderCommandTexture.Modulate)
                    {
                        _flushBatchBuffer();
                        BatchingTexture = renderCommandTexture.TextureId;
                        BatchingModulate = renderCommandTexture.Modulate;
                    }
                }
                else
                {
                    BatchingTexture = renderCommandTexture.TextureId;
                    BatchingModulate = renderCommandTexture.Modulate;
                }

                ref var batchCommand = ref BatchBuffer.AllocAdd();
                batchCommand.PositionA = renderCommandTexture.PositionA;
                batchCommand.PositionB = renderCommandTexture.PositionB;
                batchCommand.SubRegion = renderCommandTexture.SubRegion;
                batchCommand.HasSubRegion = renderCommandTexture.HasSubRegion;
                batchCommand.ArrayIndex = renderCommandTexture.ArrayIndex;
                if (_batchModelMatricesNeedsUpdate)
                {
                    _batchModelMatricesNeedsUpdate = false;
                    ref var model = ref BatchModelMatrices.AllocAdd();
                    model = _currentModelMatrix;
                }

                batchCommand.TransformIndex = BatchModelMatrices.Count - 1;

                return;
            }

            var loadedTexture = _loadedTextures[renderCommandTexture.TextureId];
            var arrayed = loadedTexture.Type == LoadedTextureType.Array2D;
            ShaderProgram program;
            if (arrayed)
            {
                program = Vertex2DArrayProgram;
            }
            else
            {
                program = Vertex2DProgram;
            }

            program.Use();
            if (arrayed)
            {
                program.SetUniform(UniModArrayIndex, (float) renderCommandTexture.ArrayIndex - 1);
            }

            GL.BindVertexArray(QuadVAO.Handle);
            // Use QuadVBO to render a single quad and modify the model matrix to position it where we need it.
            if (renderCommandTexture.HasSubRegion)
            {
                // If the texture is in an atlas we have to set modifyUV in the shader,
                // so that the shader translated the quad VBO 0->1 tex coords to the sub region needed.
                var subRegion = renderCommandTexture.SubRegion;
                var (w, h) = loadedTexture.Size;
                if (_currentSpace == CurrentSpace.ScreenSpace)
                {
                    // Flip Y texture coordinates to fix screen space flipping.
                    var vec = new Vector4(
                        subRegion.Left / w,
                        subRegion.Bottom / h,
                        subRegion.Right / w,
                        subRegion.Top / h);
                    program.SetUniform(UniModUV, vec);
                }
                else
                {
                    var vec = new Vector4(
                        subRegion.Left / w,
                        subRegion.Top / h,
                        subRegion.Right / w,
                        subRegion.Bottom / h);
                    program.SetUniform(UniModUV, vec);
                }
            }
            else
            {
                if (_currentSpace == CurrentSpace.ScreenSpace)
                {
                    // Flip Y texture coordinates to fix screen space flipping.
                    program.SetUniform(UniModUV, new Vector4(0, 1, 1, 0));
                }
                else
                {
                    program.SetUniform(UniModUV, new Vector4(0, 0, 1, 1));
                }
            }

            // Handle modulate.
            program.SetUniform(UniModulate, renderCommandTexture.Modulate);

            var rectTransform = Matrix3.Identity;
            (rectTransform.R0C0, rectTransform.R1C1) = renderCommandTexture.PositionB - renderCommandTexture.PositionA;
            (rectTransform.R0C2, rectTransform.R1C2) = renderCommandTexture.PositionA;
            rectTransform.Multiply(ref modelMatrix);
            program.SetUniform(UniModelMatrix, rectTransform);

            GL.ActiveTexture(TextureUnit.Texture0);
            if (arrayed)
            {
                GL.BindTexture(TextureTarget.Texture2DArray, loadedTexture.OpenGLObject.Handle);
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, loadedTexture.OpenGLObject.Handle);
            }

            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        /// <summary>
        ///     Flush the render handle, processing and re-pooling all the command lists.
        /// </summary>
        private void _flushRenderHandle(RenderHandle handle)
        {
            foreach (var (drawHandle, commandList) in handle._drawingHandles)
            {
                drawHandle.Dispose();
                _processCommandList(commandList);
                _returnCommandList(commandList);
            }

            handle._drawingHandles.Clear();
            _flushBatchBuffer();
            _disableScissor();
        }

        private void _flushBatchBuffer()
        {
            // TODO: Tons of things are still unimplemented.
            // Pretty much just look at _drawCommandTexture and see what's missing.
            if (BatchBuffer.Count < TextureBatchThreshold)
            {
                foreach (ref var batchCommand in BatchBuffer)
                {
                    var textureCommand = new RenderCommand
                    {
                        // ReSharper disable once PossibleInvalidOperationException
                        TextureId = BatchingTexture.Value,
                        PositionA = batchCommand.PositionA,
                        PositionB = batchCommand.PositionB,
                        SubRegion = batchCommand.SubRegion,
                        HasSubRegion = batchCommand.HasSubRegion,
                        // ReSharper disable once PossibleInvalidOperationException
                        Modulate = BatchingModulate.Value,
                        ArrayIndex = batchCommand.ArrayIndex,
                    };
                    ref var transform = ref BatchModelMatrices[batchCommand.TransformIndex];
                    _drawCommandTexture(ref textureCommand, false, ref transform);
                }

                BatchBuffer.Clear();
                BatchModelMatrices.Clear();
                _batchModelMatricesNeedsUpdate = true;
                BatchingTexture = null;
                BatchingModulate = null;
                return;
            }

            DebugTools.Assert(BatchingTexture.HasValue);
            var loadedTexture = _loadedTextures[BatchingTexture.Value];
            var arrayed = loadedTexture.Type == LoadedTextureType.Array2D;
            ushort quadIndex = 0;
            foreach (ref var command in BatchBuffer)
            {
                ref var transform = ref BatchModelMatrices[command.TransformIndex];
                UIBox2 sr;
                if (command.HasSubRegion)
                {
                    var (w, h) = loadedTexture.Size;
                    var csr = command.SubRegion;
                    if (_currentSpace == CurrentSpace.WorldSpace)
                    {
                        sr = new UIBox2(csr.Left / w, csr.Top / h, csr.Right / w, csr.Bottom / h);
                    }
                    else
                    {
                        sr = new UIBox2(csr.Left / w, csr.Bottom / h, csr.Right / w, csr.Top / h);
                    }
                }
                else
                {
                    sr = new UIBox2(0, 0, 1, 1);
                }

                var arrayIndex = command.ArrayIndex - 1;
                var bl = transform.Transform(command.PositionA);
                var br = transform.Transform(new Vector2(command.PositionB.X, command.PositionA.Y));
                var tr = transform.Transform(command.PositionB);
                var tl = transform.Transform(new Vector2(command.PositionA.X, command.PositionB.Y));
                var vIdx = quadIndex * 4;
                BatchVertexData[vIdx + 0] = new Vertex2D(bl, sr.BottomLeft, arrayIndex);
                BatchVertexData[vIdx + 1] = new Vertex2D(br, sr.BottomRight, arrayIndex);
                BatchVertexData[vIdx + 2] = new Vertex2D(tl, sr.TopLeft, arrayIndex);
                BatchVertexData[vIdx + 3] = new Vertex2D(tr, sr.TopRight, arrayIndex);
                var nIdx = quadIndex * 5;
                var tIdx = (ushort) (quadIndex * 4);
                BatchIndexData[nIdx + 0] = tIdx;
                BatchIndexData[nIdx + 1] = (ushort) (tIdx + 1);
                BatchIndexData[nIdx + 2] = (ushort) (tIdx + 2);
                BatchIndexData[nIdx + 3] = (ushort) (tIdx + 3);
                BatchIndexData[nIdx + 4] = ushort.MaxValue;
                quadIndex += 1;
                if (quadIndex >= MaxBatchQuads)
                {
                    throw new NotImplementedException("Can't batch things this big yet sorry.");
                }
            }

            if (arrayed)
            {
                GL.BindVertexArray(BatchArrayedVAO.Handle);
            }
            else
            {
                GL.BindVertexArray(BatchVAO.Handle);
            }

            BatchVBO.Use();
            BatchEBO.Use();
            var vertexData = new Span<Vertex2D>(BatchVertexData, 0, quadIndex * 4);
            var indexData = new Span<ushort>(BatchIndexData, 0, quadIndex * 5);
            if (_reallocateBuffers)
            {
                BatchVBO.Reallocate(vertexData);
                BatchEBO.Reallocate(indexData);
            }
            else
            {
                BatchVBO.WriteSubData(vertexData);
                BatchEBO.WriteSubData(indexData);
            }

            // Bind atlas texture.
            GL.ActiveTexture(TextureUnit.Texture0);
            ShaderProgram program;
            if (arrayed)
            {
                GL.BindTexture(TextureTarget.Texture2DArray, loadedTexture.OpenGLObject.Handle);
                program = Vertex2DArrayProgram;
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, loadedTexture.OpenGLObject.Handle);
                program = Vertex2DProgram;
            }

            program.Use();

            if (arrayed)
            {
                program.SetUniform(UniModArrayIndex, 1f);
            }

            // Model matrix becomes identity since it's built into the batch mesh.
            program.SetUniform(UniModelMatrix, Matrix3.Identity);
            // Reset ModUV to ensure it's identity and doesn't touch anything.
            program.SetUniform(UniModUV, new Vector4(0, 0, 1, 1));
            // Set modulate.
            DebugTools.Assert(BatchingModulate.HasValue);
            program.SetUniform(UniModulate, BatchingModulate.Value);
            // Enable primitive restart & do that draw.
            GL.Enable(EnableCap.PrimitiveRestart);
            GL.PrimitiveRestartIndex(ushort.MaxValue);
            GL.DrawElements(PrimitiveType.TriangleStrip, quadIndex * 5, DrawElementsType.UnsignedShort, 0);
            GL.Disable(EnableCap.PrimitiveRestart);

            // Reset batch buffer.
            BatchBuffer.Clear();
            BatchModelMatrices.Clear();
            _batchModelMatricesNeedsUpdate = true;
            BatchingTexture = null;
            BatchingModulate = null;

            if (_logPoolOverdraw && _poolListCreated > 0)
            {
                Logger.DebugS("ogl", "pool overdraw: l: {0}", _poolListCreated);
            }

            _poolListCreated = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void _disableScissor()
        {
            if (_isScissoring)
            {
                GL.Disable(EnableCap.ScissorTest);
            }
            _isScissoring = false;
        }

        private class RenderHandle : IRenderHandle, IDisposable
        {
            private readonly Clyde _manager;

            public readonly List<(DrawingHandle, RenderCommandList)> _drawingHandles =
                new List<(DrawingHandle, RenderCommandList)>();

            private bool _disposed;

            public RenderHandle(Clyde manager)
            {
                _manager = manager;
            }

            public DrawingHandleWorld CreateHandleWorld()
            {
                _assertNotDisposed();
                if (_manager._currentSpace != CurrentSpace.WorldSpace)
                {
                    throw new InvalidOperationException(
                        "Cannot create world drawing handle while not drawing in world space.");
                }

                var handle = new DrawingHandleWorld(this, _drawingHandles.Count);
                _drawingHandles.Add((handle, _manager._getNewCommandList()));
                return handle;
            }

            public DrawingHandleScreen CreateHandleScreen()
            {
                _assertNotDisposed();
                if (_manager._currentSpace != CurrentSpace.ScreenSpace)
                {
                    throw new InvalidOperationException(
                        "Cannot create world drawing handle while not drawing in screen space.");
                }

                var handle = new DrawingHandleScreen(this, _drawingHandles.Count);
                _drawingHandles.Add((handle, _manager._getNewCommandList()));
                return handle;
            }

            public void SetModelTransform(ref Matrix3 matrix, int handleId)
            {
                _assertNotDisposed();

                var list = _drawingHandles[handleId].Item2;
                ref var command = ref list.RenderCommands.AllocAdd();
                command.Type = RenderCommandType.Transform;
                command.Matrix = matrix;
            }

            public void DrawTextureRect(Texture texture, Vector2 a, Vector2 b, Color modulate, UIBox2? subRegion,
                int handleId)
            {
                _assertNotDisposed();

                var list = _drawingHandles[handleId].Item2;
                ref var command = ref list.RenderCommands.AllocAdd();
                command.Type = RenderCommandType.Texture;
                switch (texture)
                {
                    case AtlasTexture atlas:
                    {
                        texture = atlas.SourceTexture;
                        if (subRegion.HasValue)
                        {
                            var offset = atlas.SubRegion.TopLeft;
                            subRegion = new UIBox2(
                                subRegion.Value.TopLeft + offset,
                                subRegion.Value.BottomRight + offset);
                        }
                        else
                        {
                            subRegion = atlas.SubRegion;
                        }

                        break;
                    }
                }

                var openGLTexture = (OpenGLTexture) texture;
                command.TextureId = openGLTexture.OpenGLTextureId;
                command.ArrayIndex = openGLTexture.ArrayIndex;
                command.PositionA = a;
                command.PositionB = b;
                command.Modulate = modulate;
                if (subRegion.HasValue)
                {
                    command.SubRegion = subRegion.Value;
                    command.HasSubRegion = true;
                }
                else
                {
                    command.HasSubRegion = false;
                }
            }

            public void SetScissor(in UIBox2i? scissorBox, int handleId)
            {
                var list = _drawingHandles[handleId].Item2;
                ref var command = ref list.RenderCommands.AllocAdd();

                command.Type = RenderCommandType.Scissor;
                command.EnableScissor = scissorBox.HasValue;
                if (scissorBox.HasValue)
                {
                    command.Scissor = scissorBox.Value;
                }
            }

            public void Dispose()
            {
                _disposed = true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void _assertNotDisposed()
            {
                DebugTools.Assert(!_disposed);
            }
        }

        private enum CurrentSpace
        {
            ScreenSpace,
            WorldSpace,
        }

        // Use a tagged union to store all render commands.
        // This significantly improves performance vs doing sum types via inheritance.
        [StructLayout(LayoutKind.Explicit)]
        private struct RenderCommand
        {
            [FieldOffset(0)] public RenderCommandType Type;

            // Texture command fields.
            [FieldOffset(4)] public int TextureId;
            [FieldOffset(8)] public Vector2 PositionA;
            [FieldOffset(16)] public Vector2 PositionB;

            [FieldOffset(24)] public UIBox2 SubRegion;

            // UIBox2 is 4 floats
            [FieldOffset(40)] public bool HasSubRegion;
            [FieldOffset(44)] public Color Modulate;
            [FieldOffset(60)] public int ArrayIndex;

            // Transform command fields.
            [FieldOffset(4)] public Matrix3 Matrix;

            // Scissor command fields.
            [FieldOffset(4)] public bool EnableScissor;
            [FieldOffset(8)] public UIBox2i Scissor;
        }

        private struct BatchCommand
        {
            public Vector2 PositionA;
            public Vector2 PositionB;
            public UIBox2 SubRegion;
            public bool HasSubRegion;
            public int ArrayIndex;
            public int TransformIndex;
        }

        private enum RenderCommandType : int
        {
            Texture,
            Transform,
            Scissor,
        }

        /// <summary>
        ///     A list of rendering commands to execute in order. Pooled.
        /// </summary>
        // ReSharper disable once ClassNeverInstantiated.Local
        private class RenderCommandList
        {
            public RefList<RenderCommand> RenderCommands = new RefList<RenderCommand>();
        }
    }

    /// <summary>
    ///     Handle to generate command lists inside the OpenGL rendering system.
    /// </summary>
    internal interface IRenderHandle
    {
        DrawingHandleWorld CreateHandleWorld();
        DrawingHandleScreen CreateHandleScreen();

        void SetModelTransform(ref Matrix3 matrix, int handleId);
        void DrawTextureRect(Texture texture, Vector2 a, Vector2 b, Color modulate, UIBox2? subRegion, int handleId);
        void SetScissor(in UIBox2i? scissorBox, int handleId);
    }
}
