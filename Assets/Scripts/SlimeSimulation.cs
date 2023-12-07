using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ComputeUtilities;
using Unity.VisualScripting;
using TMPro;
using UnityEngine.UIElements;
using System.Security.Cryptography;

public class SlimeSimulation : MonoBehaviour
{
    // UI Vars
    public RawImage viewport;

    public bool playing = false;
    public bool placingFood = false;
    public bool erasing = false;
    public bool clearAll = false;

    public TMP_Text togglePlayText;
    public TMP_Text toggleFoodText;
    public TMP_Text toggleEraseText;

    public SimulationSettings settings;

    public ComputeShader computeSim;

    const int updateKernel = 0;
    const int blurKernel = 1;
    const int paintKernel = 2;
    const int foodKernel = 3;
    const int eraseKernel = 4;
    const int clearKernel = 5;

    public RenderTexture viewportTex;
    public RenderTexture trailMap;
    public RenderTexture nextTrailMap;
    public RenderTexture foodMap;

    public List<SlimeAgent> agents;
    public HashSet<Vector2> foodSources = new();
    public SlimeAgent[] agentArray;
    public Vector2[] foodSourceArray;
    ComputeBuffer agentBuffer;
    ComputeBuffer speciesBuffer;
    ComputeBuffer foodBuffer;

    // Start is called before the first frame update
    void Start()
    {
        // Initialize agent, trail, and color buffers
        ComputeUtil.CreateTex(ref viewportTex, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref trailMap, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref nextTrailMap, settings.vpWidth, settings.vpHeight);
        ComputeUtil.CreateTex(ref foodMap, settings.vpWidth, settings.vpHeight);

        viewport.texture = viewportTex;

        // update function in compute shader
        computeSim.SetTexture(updateKernel, "TrailMap", trailMap);

        // blur function in compute shader
        computeSim.SetTexture(blurKernel, "TrailMap", trailMap);
        computeSim.SetTexture(blurKernel, "NextTrailMap", nextTrailMap);

        // paint canvas function in compute shader
        computeSim.SetTexture(paintKernel, "ViewportTex", viewportTex);
        computeSim.SetTexture(paintKernel, "TrailMap", trailMap);
        computeSim.SetTexture(paintKernel, "FoodMap", foodMap);

        // paint food function in compute shader
        computeSim.SetTexture(foodKernel, "FoodMap", foodMap);

        // erase function in compute shader
        computeSim.SetTexture(eraseKernel, "TrailMap", trailMap);
        computeSim.SetTexture(eraseKernel, "FoodMap", foodMap);

        // clearing trail, food, and viewport textures (setting to <0,0,0,0>) in compute shader
        computeSim.SetTexture(clearKernel, "TrailMap", trailMap);
        computeSim.SetTexture(clearKernel, "FoodMap", foodMap);
        computeSim.Dispatch(clearKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);

        CreateAgents();

        ComputeUtil.CreateBuffer(ref speciesBuffer, settings.species);
        computeSim.SetBuffer(updateKernel, "species", speciesBuffer);
        computeSim.SetBuffer(paintKernel, "species", speciesBuffer);

        computeSim.SetInt("width", settings.vpWidth);
        computeSim.SetInt("height", settings.vpHeight);

        computeSim.SetFloat("decayRate", settings.decayRate);
        computeSim.SetFloat("diffuseRate", settings.diffuseRate);

        computeSim.SetInt("foodBrushRadius", settings.foodBrushRadius);
        computeSim.SetVector("foodColor", settings.foodColor);

        computeSim.SetInt("eraseBrushRadius", settings.eraseBrushRadius);

        Simulate();
        Paint();
    }

    public void CreateAgents()
    {
        // intialize agent positions within circle 
        agents = new List<SlimeAgent>(settings.numAgents);
        for (int i = 0; i < settings.numAgents; i++)
        {
            float randomTheta = (float)(Random.value) * 2 * Mathf.PI;
            float randomR = (float)(Random.value) * 250;
            float randomOffsetX = Mathf.Cos(randomTheta) * randomR;
            float randomOffsetY = Mathf.Sin(randomTheta) * randomR;
            float randAngle = Mathf.PI + Mathf.Atan2(randomOffsetY, randomOffsetX);
            agents.Add(new SlimeAgent
            {
                position = new Vector2(settings.vpWidth / 2 + randomOffsetX, settings.vpHeight / 2 + randomOffsetY),
                angle = randAngle,
                speciesID = 0
            });
        }

        SetAgents();
    }

    public void SetAgents()
    {
        agentArray = agents.ToArray();

        // passing agent data + other uniforms
        ComputeUtil.CreateBuffer(ref agentBuffer, agentArray);
        computeSim.SetBuffer(updateKernel, "slimeAgents", agentBuffer);
        computeSim.SetInt("numAgents", agentArray.Length);
    }

    public void SetFood()
    {
        foodSourceArray = new Vector2[foodSources.Count];
        foodSources.CopyTo(foodSourceArray);

        // passing food data + other uniforms
        ComputeUtil.CreateBuffer(ref foodBuffer, foodSourceArray);
        computeSim.SetBuffer(updateKernel, "foodSources", foodBuffer);
        computeSim.SetInt("numFoodSources", foodSourceArray.Length);
    }

    public void TogglePlaying()
    {
        playing = !playing;

        if (playing)
        {
            togglePlayText.SetText("Pause");
        }
        else
        {
            togglePlayText.SetText("Play");
        }
    }

    public void ToggleFood()
    {
        // force Unity to recompile
        placingFood = !placingFood;

        if (placingFood)
        {
            toggleFoodText.SetText("Stop");
        }
        else
        {
            toggleFoodText.SetText("Place Food");
        }
    }

    public void ToggleErase()
    {
        erasing = !erasing;

        if (erasing)
        {
            toggleEraseText.SetText("Stop");
        }
        else
        {
            toggleEraseText.SetText("Erase");
        }
    }

    void FixedUpdate()
    {
        // place food if placingFood is true and mouse is clicked
        if (placingFood && Input.GetButton("Fire1"))
        {
            PlaceFood();
        }

        // run the specified number of simulations per frame
        if (playing)
        {
            for (int i = 0; i < settings.simsPerFrame; i++)
            {
                Simulate();
            }

            Paint();
        }

        // erase if erasing is true and mouse is clicked
        if (erasing && Input.GetButton("Fire1"))
        {
            Erase();
        }
    }

    void PlaceFood()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);

        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        bool withinCanvas = viewport.rectTransform.rect.Contains(canvasPos);

        // if the click was within the canvas, pass the click position to the compute shader and paint food
        if (withinCanvas)
        {
            Vector2 shiftedCanvasPos = canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2);

            if (!foodSources.Contains(shiftedCanvasPos))
            {
                foodSources.Add(shiftedCanvasPos);
                SetFood();
                computeSim.SetVector("clickPos", shiftedCanvasPos);
                computeSim.Dispatch(foodKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
            }
        }

        Paint();
    }

    void Erase()
    {
        // store position of click in screen space
        Vector2 screenPos = new(Input.mousePosition.x, Input.mousePosition.y);

        // convert screen space click position to the coordinate space of the viewport
        RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport.rectTransform, screenPos, null, out Vector2 canvasPos);
        bool inCanvas = viewport.rectTransform.rect.Contains(canvasPos);

        Vector2 clickPos = canvasPos + new Vector2(settings.vpWidth / 2, settings.vpHeight / 2);

        agentBuffer.GetData(agentArray);

        for (int i = 0; i < agents.Count; i++)
        {
            agents[i] = agentArray[i];
        }

        for (int i = 0; i < agents.Count; i++)
        {
            if ((agents[i].position - clickPos).magnitude <= settings.eraseBrushRadius)
            {
                agents.RemoveAt(i);
                i--;
            }
        }

        SetAgents();

        // if the click was within the canvas, pass the click position to the compute shader and erase
        if (inCanvas)
        {
            computeSim.SetVector("clickPos", clickPos);
            computeSim.Dispatch(eraseKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        }

        Paint();
    }

    void ClearAll()
    {
        computeSim.Dispatch(clearKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
    }

    void Paint()
    {
        computeSim.SetTexture(paintKernel, "FoodMap", foodMap);
        computeSim.Dispatch(paintKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
    }

    void Simulate()
    {
        computeSim.SetFloat("dt", Time.fixedDeltaTime);
        computeSim.SetFloat("time", Time.fixedTime);

        // send species related buffers to shader here, for now just using magic values within compute
        computeSim.Dispatch(updateKernel, Mathf.CeilToInt(agentArray.Length / 16.0F), 1, 1);
        computeSim.Dispatch(blurKernel, settings.vpWidth / 8, settings.vpHeight / 8, 1);
        Graphics.Blit(nextTrailMap, trailMap);
    }


    // Called when the attached Object is destroyed.
    void OnDestroy()
    {
        // Release buffers
        agentBuffer.Release();
        speciesBuffer.Release();
    }
}
