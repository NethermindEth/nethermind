-- Generated from admitted Roslyn operations; do not edit.
namespace SimpleTransferCompletionExtractor.StageB.Control.Generated

structure TickResult where
  exhausted : Bool
  remainingFuel : Int
  deriving DecidableEq, Repr

structure EdgeResult where
  kind : Int
  destination : Int
  deriving DecidableEq, Repr

def wrap64 (value : Int) : Int := (value + 9223372036854775808) % 18446744073709551616 - 9223372036854775808

structure BlockCursor (α : Type) where
  ordinal : Int
  carried : α
  deriving DecidableEq, Repr

structure FinishResult (α : Type) where
  kind : Int
  cursor : BlockCursor α
  deriving DecidableEq, Repr

def Tick (remainingFuel : Int) : TickResult :=
  (if (remainingFuel == (0 : Int)) then (TickResult.mk true (0 : Int)) else (TickResult.mk false (wrap64 (remainingFuel - (1 : Int)))))

def SelectEdge (conditionKind : String) (valueIsBoolean : Bool) (booleanValue : Bool) (hasFallThrough : Bool) (fallThroughDestination : Int) (fallThroughReturns : Bool) (hasConditional : Bool) (conditionalDestination : Int) (conditionalReturns : Bool) : EdgeResult :=
  (if ((conditionKind != "None") && (!valueIsBoolean)) then (EdgeResult.mk (2 : Int) (-(1 : Int))) else (let takeConditional := (hasConditional && (((conditionKind == "WhenTrue") && booleanValue) || ((conditionKind == "WhenFalse") && (!booleanValue)))); (let hasEdge := (if takeConditional then hasConditional else hasFallThrough); (if (!hasEdge) then (EdgeResult.mk (3 : Int) (-(1 : Int))) else (let destination := (if takeConditional then conditionalDestination else fallThroughDestination); (let returns := (if takeConditional then conditionalReturns else fallThroughReturns); (if (decide (destination < (0 : Int))) then (EdgeResult.mk (if returns then (1 : Int) else (4 : Int)) (-(1 : Int))) else (EdgeResult.mk (0 : Int) destination))))))))

def FinishBlock {α : Type} (exit : Int) (cursor : BlockCursor α) (conditionKind : String) (valueIsBoolean : Bool) (booleanValue : Bool) (hasFallThrough : Bool) (fallThroughDestination : Int) (fallThroughReturns : Bool) (hasConditional : Bool) (conditionalDestination : Int) (conditionalReturns : Bool) : FinishResult α :=
  (if (exit == (1 : Int)) then (FinishResult.mk (1 : Int) cursor) else (if (exit == (2 : Int)) then (FinishResult.mk (5 : Int) cursor) else (let edge := (SelectEdge conditionKind valueIsBoolean booleanValue hasFallThrough fallThroughDestination fallThroughReturns hasConditional conditionalDestination conditionalReturns); (FinishResult.mk (edge).kind (if ((edge).kind == (0 : Int)) then (BlockCursor.mk (edge).destination (cursor).carried) else cursor)))))

def SelectLocalReturn (constructing : Bool) (valueMode : Bool) : Int :=
  (if (!constructing) then (0 : Int) else (if valueMode then (1 : Int) else (2 : Int)))

def ShouldReadTransparent (preservesOperand : Bool) (valueMode : Bool) : Bool :=
  ((!preservesOperand) && valueMode)

end SimpleTransferCompletionExtractor.StageB.Control.Generated
