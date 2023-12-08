using UnityEngine;

public struct FoodSource
{
    public Vector2 position;
    public float attractorStrength; // set to zero to disable attraction to food source
    public int amount; // possibly use later for food source depletion
}