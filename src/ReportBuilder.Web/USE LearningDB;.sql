USE LearningDB;

DECLARE @q VECTOR(1536) =
    (SELECT Embedding FROM dbo.ProductMaster WHERE [Product name] LIKE 'masala%');

SELECT TOP 3 Id, [Product name], [Retail Rate],
       VECTOR_DISTANCE('cosine', Embedding, @q) AS distance
FROM dbo.ProductMaster
ORDER BY distance;