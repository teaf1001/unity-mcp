"""
Compile Monitor Tool - Monitors Unity compilation status and provides detailed error reporting.
"""
from typing import Annotated, Any, Literal
import time

from fastmcp import Context
from registry import mcp_for_unity_tool
from unity_connection import send_command_with_retry


@mcp_for_unity_tool(
    description="Monitor Unity compilation status and get detailed error reports. Provides real-time compilation monitoring and error analysis."
)
def compile_monitor(
    ctx: Context,
    action: Annotated[Literal["get_status", "wait_for_complete", "get_errors", "get_warnings", "clear_errors", "force_recompile"], "Compilation monitoring actions"],
    timeout_seconds: Annotated[int, "Timeout in seconds for wait operations"] | None = None,
    include_stack_trace: Annotated[bool, "Include stack traces in error reports"] | None = None,
) -> dict[str, Any]:
    ctx.info(f"Processing compile_monitor: {action}")
    
    try:
        if action == "get_status":
            return get_compile_status(ctx)
        elif action == "wait_for_complete":
            return wait_for_compilation_complete(ctx, timeout_seconds or 30)
        elif action == "get_errors":
            return get_compilation_errors(ctx, include_stack_trace or False)
        elif action == "get_warnings":
            return get_compilation_warnings(ctx)
        elif action == "clear_errors":
            return clear_compilation_errors(ctx)
        elif action == "force_recompile":
            return force_recompile(ctx)
        else:
            return {"success": False, "message": f"Unknown action: {action}"}
            
    except Exception as e:
        return {"success": False, "message": f"Python error in compile_monitor: {str(e)}"}


def get_compile_status(ctx: Context) -> dict[str, Any]:
    """Get current compilation status."""
    try:
        # Get editor state
        editor_response = send_command_with_retry("manage_editor", {"action": "get_state"})
        if not isinstance(editor_response, dict) or not editor_response.get("success"):
            return {"success": False, "message": "Failed to get editor state"}
        
        editor_data = editor_response.get("data", {})
        is_compiling = editor_data.get("isCompiling", False)
        is_updating = editor_data.get("isUpdating", False)
        
        # Get console errors
        console_response = send_command_with_retry("read_console", {"action": "get", "count": 50})
        errors = []
        warnings = []
        
        if isinstance(console_response, dict) and console_response.get("success"):
            console_data = console_response.get("data", [])
            for entry in console_data:
                if entry.get("type") == "Error":
                    errors.append({
                        "message": entry.get("message", ""),
                        "file": entry.get("file", ""),
                        "line": entry.get("line", ""),
                        "stackTrace": entry.get("stackTrace", "")
                    })
                elif entry.get("type") == "Warning":
                    warnings.append({
                        "message": entry.get("message", ""),
                        "file": entry.get("file", ""),
                        "line": entry.get("line", "")
                    })
        
        return {
            "success": True,
            "message": "Compilation status retrieved",
            "data": {
                "isCompiling": is_compiling,
                "isUpdating": is_updating,
                "hasErrors": len(errors) > 0,
                "hasWarnings": len(warnings) > 0,
                "errorCount": len(errors),
                "warningCount": len(warnings),
                "errors": errors,
                "warnings": warnings,
                "status": "compiling" if is_compiling else ("updating" if is_updating else "idle")
            }
        }
    except Exception as e:
        return {"success": False, "message": f"Error getting compile status: {str(e)}"}


def wait_for_compilation_complete(ctx: Context, timeout_seconds: int) -> dict[str, Any]:
    """Wait for compilation to complete."""
    try:
        start_time = time.time()
        
        while time.time() - start_time < timeout_seconds:
            status_response = get_compile_status(ctx)
            if not status_response.get("success"):
                return status_response
            
            status_data = status_response.get("data", {})
            if not status_data.get("isCompiling", False) and not status_data.get("isUpdating", False):
                return {
                    "success": True,
                    "message": "Compilation completed",
                    "data": {
                        "waitTime": time.time() - start_time,
                        "finalStatus": status_data
                    }
                }
            
            time.sleep(0.5)  # Check every 500ms
        
        return {
            "success": False,
            "message": f"Compilation timeout after {timeout_seconds} seconds",
            "data": {"timeout": True}
        }
    except Exception as e:
        return {"success": False, "message": f"Error waiting for compilation: {str(e)}"}


def get_compilation_errors(ctx: Context, include_stack_trace: bool) -> dict[str, Any]:
    """Get detailed compilation errors."""
    try:
        console_response = send_command_with_retry("read_console", {"action": "get", "count": 100})
        errors = []
        
        if isinstance(console_response, dict) and console_response.get("success"):
            console_data = console_response.get("data", [])
            for entry in console_data:
                if entry.get("type") == "Error":
                    error_info = {
                        "message": entry.get("message", ""),
                        "file": entry.get("file", ""),
                        "line": entry.get("line", "")
                    }
                    if include_stack_trace and entry.get("stackTrace"):
                        error_info["stackTrace"] = entry.get("stackTrace", "")
                    errors.append(error_info)
        
        return {
            "success": True,
            "message": f"Retrieved {len(errors)} compilation errors",
            "data": {
                "errorCount": len(errors),
                "errors": errors
            }
        }
    except Exception as e:
        return {"success": False, "message": f"Error getting compilation errors: {str(e)}"}


def get_compilation_warnings(ctx: Context) -> dict[str, Any]:
    """Get compilation warnings."""
    try:
        console_response = send_command_with_retry("read_console", {"action": "get", "count": 100})
        warnings = []
        
        if isinstance(console_response, dict) and console_response.get("success"):
            console_data = console_response.get("data", [])
            for entry in console_data:
                if entry.get("type") == "Warning":
                    warnings.append({
                        "message": entry.get("message", ""),
                        "file": entry.get("file", ""),
                        "line": entry.get("line", "")
                    })
        
        return {
            "success": True,
            "message": f"Retrieved {len(warnings)} compilation warnings",
            "data": {
                "warningCount": len(warnings),
                "warnings": warnings
            }
        }
    except Exception as e:
        return {"success": False, "message": f"Error getting compilation warnings: {str(e)}"}


def clear_compilation_errors(ctx: Context) -> dict[str, Any]:
    """Clear console errors."""
    try:
        console_response = send_command_with_retry("read_console", {"action": "clear"})
        return console_response if isinstance(console_response, dict) else {"success": False, "message": str(console_response)}
    except Exception as e:
        return {"success": False, "message": f"Error clearing compilation errors: {str(e)}"}


def force_recompile(ctx: Context) -> dict[str, Any]:
    """Force Unity to recompile all scripts."""
    try:
        # Use menu item to force recompilation
        menu_response = send_command_with_retry("execute_menu_item", {"menuPath": "Assets/Refresh"})
        if isinstance(menu_response, dict) and menu_response.get("success"):
            return {
                "success": True,
                "message": "Forced recompilation initiated",
                "data": {"recompileTriggered": True}
            }
        return menu_response if isinstance(menu_response, dict) else {"success": False, "message": str(menu_response)}
    except Exception as e:
        return {"success": False, "message": f"Error forcing recompilation: {str(e)}"}
