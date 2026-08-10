using System;
using System.Collections.Generic;
using TuiDwm.Core;

namespace TuiDwm.Canvas;

// Manages N terminal sessions and a kinematic position within them.
//
// Sessions are laid out as a 1D carousel: session[0] at offset 0, session[1] at offset 1, etc.
// _position is a float in [0, N-1]. The visible session is the one nearest _position;
// adjacent sessions appear sliding in from either side during a fling.
//
// Touch model:
//   PointerDown  → anchor: record start position and current _position
//   PointerUpdate → pan: _position = anchor + delta / sessionWidth
//   PointerUp    → fling: apply velocity, let spring settle to nearest integer
//
// No tabs. No scrollbars. Just momentum.
public sealed class KinematicSwitcher
{
    private readonly List<TerminalSession> _sessions = new();
    private float _position;      // float index into _sessions
    private float _velocity;      // sessions/second
    private bool  _isDragging;
    private float _dragAnchorPos;
    private int   _dragAnchorX;
    private int   _dragPointerId;

    private const float Friction   = 8f;   // damping per second
    private const float SnapSpring = 20f;  // spring strength when settling
    private const float FlingScale = 0.003f; // px → sessions/second conversion

    public int SessionCount => _sessions.Count;
    public int ActiveIndex  => (int)Math.Round(Math.Clamp(_position, 0, _sessions.Count - 1));

    public TerminalSession? Active => _sessions.Count > 0 ? _sessions[ActiveIndex] : null;

    // ── Sessions ──────────────────────────────────────────────────────────
    public void AddSession(TerminalSession session)
    {
        _sessions.Add(session);
    }

    public void RemoveSession(TerminalSession session)
    {
        int idx = _sessions.IndexOf(session);
        if (idx < 0) return;
        _sessions.RemoveAt(idx);
        session.Dispose();
        _position = Math.Clamp(_position, 0, Math.Max(0, _sessions.Count - 1));
    }

    // ── Touch input ───────────────────────────────────────────────────────
    public void OnPointerDown(PointerEvent e)
    {
        if (!e.IsTouch) return;
        _isDragging     = true;
        _dragAnchorPos  = _position;
        _dragAnchorX    = e.X;
        _dragPointerId  = (int)e.PointerId;
        _velocity       = 0;
    }

    public void OnPointerUpdate(PointerEvent e, int clientWidth)
    {
        if (!_isDragging || (int)e.PointerId != _dragPointerId) return;
        int deltaX  = e.X - _dragAnchorX;
        // Negative deltaX = swipe left = move to higher index
        _position   = _dragAnchorPos - (float)deltaX / clientWidth;
        _position   = Math.Clamp(_position, -0.3f, _sessions.Count - 0.7f);
    }

    public void OnPointerUp(PointerEvent e, int clientWidth)
    {
        if (!_isDragging || (int)e.PointerId != _dragPointerId) return;
        _isDragging = false;

        // Estimate fling velocity from last delta
        int deltaX = e.X - _dragAnchorX;
        _velocity = -(deltaX * FlingScale * 60f); // rough: 60fps equivalent
    }

    // ── Physics tick (call once per frame) ────────────────────────────────
    public void Tick(float dt)
    {
        if (_sessions.Count == 0) return;
        if (_isDragging) return;

        int nearest = (int)Math.Round(_position);
        nearest     = Math.Clamp(nearest, 0, _sessions.Count - 1);
        float error = nearest - _position;

        if (Math.Abs(_velocity) < 0.05f && Math.Abs(error) < 0.01f)
        {
            // Snapped — lock exactly
            _position = nearest;
            _velocity = 0;
            return;
        }

        // Apply spring force toward nearest integer when velocity is low
        if (Math.Abs(_velocity) < 1.5f)
            _velocity += error * SnapSpring * dt;

        // Friction
        _velocity -= _velocity * Friction * dt;

        _position += _velocity * dt;
        _position  = Math.Clamp(_position, -0.1f, _sessions.Count - 0.9f);
    }

    // ── Render: returns (session, offsetX) pairs for visible sessions ─────
    // offsetX is in normalized [-1, 1] space (for the GPU to translate)
    public void GetVisibleSessions(Action<TerminalSession, float> visitor)
    {
        for (int i = 0; i < _sessions.Count; i++)
        {
            float offset = i - _position;
            if (Math.Abs(offset) > 1.5f) continue; // too far off screen
            visitor(_sessions[i], offset);
        }
    }
}
